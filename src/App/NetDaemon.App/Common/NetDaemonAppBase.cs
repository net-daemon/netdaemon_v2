﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetDaemon.Common.Exceptions;
using NetDaemon.Daemon.Services;

namespace NetDaemon.Common
{
    /// <summary>
    ///     Base class for all NetDaemon App types
    /// </summary>
    public abstract class NetDaemonAppBase : INetDaemonAppBase, IApplicationMetadata
    {
        private IPersistenceService? _persistenceService;
        private RuntimeInfoManager? _runtimeInfoManager;

        /// <summary>
        ///     A set of properties found in static analysis of code for each app
        /// </summary>
        public static Dictionary<string, Dictionary<string, string>> CompileTimeProperties { get; } = new();

        /// <summary>
        ///     All actions being performed for service call events
        /// </summary>
        public IList<(string, string, Func<dynamic?, Task>)> DaemonCallBacksForServiceCalls { get; }
            = new List<(string, string, Func<dynamic?, Task>)>();

        // This is declared as static since it will contain state shared globally
        private static readonly ConcurrentDictionary<string, object> _global = new();

        private readonly CancellationTokenSource _cancelSource = new();
        private bool _isDisposed;

        /// <inheritdoc/>
        public ConcurrentDictionary<string, object> Global => _global;

        /// <inheritdoc/>
        [SuppressMessage("", "CA1065")]
        public IHttpHandler Http => Daemon?.Http ?? throw new NetDaemonNullReferenceException($"{nameof(Daemon)} cant be null!");

        /// <summary>
        ///    Dependencies on other applications that will be initialized before this app
        /// </summary>
        public IEnumerable<string> Dependencies { get; set; } = new List<string>();

        /// <inheritdoc/>
        public string? Id { get; set; }

        /// <inheritdoc/>
        public bool IsEnabled { get; set; } = true;

        /// <inheritdoc/>
        public string Description
        {
            get
            {
                var appKey = GetType().FullName;

                if (appKey is null)
                    return "";

                if (CompileTimeProperties.ContainsKey(appKey) &&
                    CompileTimeProperties[appKey].ContainsKey("description"))
                {
                    return CompileTimeProperties[appKey]["description"];
                }

                return "";
            }
        }

        /// <inheritdoc/>
        public string EntityId => $"switch.netdaemon_{Id?.ToSafeHomeAssistantEntityId()}";

        /// <inheritdoc/>
        public Type ApplicationType => GetType();

        /// <inheritdoc/>
        public ILogger? Logger { get; set; }

        /// <inheritdoc/>
        [SuppressMessage("", "CA1065")]
        public dynamic Storage => _persistenceService!.Storage;

        /// <summary>
        ///     Initializes the app, is virtual and overridden
        /// </summary>
        public virtual void Initialize()
        {
            // do nothing
        }

        /// <summary>
        ///     Initializes the app async, is virtual and overridden
        /// </summary>
        public virtual Task InitializeAsync()
        {
            // Do nothing
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task RestoreAppStateAsync() => _persistenceService!.RestoreAppStateAsync();

        /// <inheritdoc/>
        public AppRuntimeInfo RuntimeInfo { get; } = new() { HasError = false };

        /// <inheritdoc/>
        [SuppressMessage("", "CA1065")]
        public IEnumerable<string> EntityIds => Daemon?.State.Select(n => n.EntityId) ??
                                                throw new NetDaemonNullReferenceException(
                                                    "Daemon not expected to be null");

        /// <summary>
        ///     Instance to Daemon service
        /// </summary>
        protected INetDaemon? Daemon { get; set; }

        /// <inheritdoc/>
        public IServiceProvider? ServiceProvider { get; internal set; }

        /// <inheritdoc/>
        public void SaveAppState() => _persistenceService!.SaveAppState();

        /// <inheritdoc/>
        public void Speak(string entityId, string message)
        {
            _ = Daemon ?? throw new NetDaemonNullReferenceException($"{nameof(Daemon)} cant be null!");
            Daemon!.Speak(entityId, message);
        }

        /// <inheritdoc/>
        public virtual Task StartUpAsync(INetDaemon daemon)
        {
            _ = daemon ?? throw new NetDaemonArgumentNullException(nameof(daemon));
            Daemon = daemon;
            Logger = daemon.Logger;
            ServiceProvider ??= daemon.ServiceProvider!;

            _runtimeInfoManager = new(daemon, this);
            _persistenceService = new ApplicationPersistenceService(this, daemon);

            Logger.LogDebug("Startup: {App}", GetUniqueIdForStorage());

            var appInfo = Daemon!.State.FirstOrDefault(s => s.EntityId == EntityId);
            if (appInfo?.State is not string appState || (appState != "on" && appState != "off"))
            {
                IsEnabled = true;
            }
            else
            {
                IsEnabled = appState == "on";
            }

            UpdateRuntimeInformation();
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Returns unique Id for instance
        /// </summary>
        [SuppressMessage("Microsoft.Globalization", "CA1308")]
        public string GetUniqueIdForStorage() => $"{GetType().Name}_{Id}".ToLowerInvariant();

        /// <summary>
        ///     Async disposable support
        /// </summary>
        public async virtual ValueTask DisposeAsync()
        {
            lock (_cancelSource)
            {
                if (_isDisposed)
                    return;
                _isDisposed = true;
            }

            if (_runtimeInfoManager != null) await _runtimeInfoManager.DisposeAsync().ConfigureAwait(false);

            _cancelSource.Cancel();

            DaemonCallBacksForServiceCalls.Clear();

            IsEnabled = false;
            _cancelSource.Dispose();
            Daemon = null;
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc/>
        public INetDaemonAppBase? GetApp(string appInstanceId)
        {
            _ = Daemon ?? throw new NetDaemonNullReferenceException($"{nameof(Daemon)} cant be null!");
            return Daemon!.GetApp(appInstanceId);
        }

        /// <inheritdoc/>
        [SuppressMessage("Microsoft.Design", "CA1062")]
        [SuppressMessage("Microsoft.Design", "CA1308")]
        public void ListenServiceCall(string domain, string service, Func<dynamic?, Task> action)
            => DaemonCallBacksForServiceCalls.Add((domain.ToLowerInvariant(), service.ToLowerInvariant(), action));

        #region -- Logger helpers --

        /// <inheritdoc/>
        public void Log(string message) => Log(LogLevel.Information, message);

        /// <inheritdoc/>
        public void Log(Exception exception, string message) => Log(LogLevel.Information, exception, message);

        /// <inheritdoc/>
        public void Log(LogLevel level, string message, params object[] param)
        {
            if (Logger is null)
            {
                return;
            }

            if (param?.Length > 0)
            {
                var result = param.Prepend(Id).ToArray();
                Logger.Log(level, $"  {{Id}}: {message}", result);
            }
            else
            {
                Logger.Log(level, $"  {{Id}}: {message}", new object[] { Id ?? "" });
            }
        }

        /// <inheritdoc/>
        public void Log(string message, params object[] param) => Log(LogLevel.Information, message, param);

        /// <inheritdoc/>
        public void Log(LogLevel level, Exception exception, string message, params object[] param)
        {
            if (param?.Length > 0)
            {
                var result = param.Prepend(Id).ToArray();
                Logger?.Log(level, exception, $"  {{Id}}: {message}", result);
            }
            else
            {
                Logger?.Log(level, exception, $"  {{Id}}: {message}", new object[] { Id ?? "" });
            }
        }

        /// <inheritdoc/>
        public void Log(Exception exception, string message, params object[] param) =>
            LogInformation(exception, message, param);

        /// <inheritdoc/>
        public void LogInformation(string message) => Log(LogLevel.Information, message);

        /// <inheritdoc/>
        public void LogInformation(Exception exception, string message) =>
            Log(LogLevel.Information, exception, message);

        /// <inheritdoc/>
        public void LogInformation(string message, params object[] param) => Log(LogLevel.Information, message, param);

        /// <inheritdoc/>
        public void LogInformation(Exception exception, string message, params object[] param) =>
            Log(LogLevel.Information, exception, message, param);

        /// <inheritdoc/>
        public void LogDebug(string message) => Log(LogLevel.Debug, message);

        /// <inheritdoc/>
        public void LogDebug(Exception exception, string message) => Log(LogLevel.Debug, exception, message);

        /// <inheritdoc/>
        public void LogDebug(string message, params object[] param) => Log(LogLevel.Debug, message, param);

        /// <inheritdoc/>
        public void LogDebug(Exception exception, string message, params object[] param) =>
            Log(LogLevel.Debug, exception, message, param);

        /// <inheritdoc/>
        public void LogError(string message)
        {
            Log(LogLevel.Error, message);
            RuntimeInfo.LastErrorMessage = message;
            UpdateRuntimeInformation();
        }

        /// <inheritdoc/>
        public void LogError(Exception exception, string message)
        {
            Log(LogLevel.Error, exception, message);
            RuntimeInfo.LastErrorMessage = message;
            UpdateRuntimeInformation();
        }

        /// <inheritdoc/>
        public void LogError(string message, params object[] param)
        {
            Log(LogLevel.Error, message, param);
            RuntimeInfo.LastErrorMessage = message;
            UpdateRuntimeInformation();
        }

        /// <inheritdoc/>
        public void LogError(Exception exception, string message, params object[] param)
        {
            Log(LogLevel.Error, exception, message, param);
            RuntimeInfo.LastErrorMessage = message;
            UpdateRuntimeInformation();
        }

        /// <inheritdoc/>
        public void LogTrace(string message) => Log(LogLevel.Trace, message);

        /// <inheritdoc/>
        public void LogTrace(Exception exception, string message) => Log(LogLevel.Trace, exception, message);

        /// <inheritdoc/>
        public void LogTrace(string message, params object[] param) => Log(LogLevel.Trace, message, param);

        /// <inheritdoc/>
        public void LogTrace(Exception exception, string message, params object[] param) =>
            Log(LogLevel.Trace, exception, message, param);

        /// <inheritdoc/>
        public void LogWarning(string message) => Log(LogLevel.Warning, message);

        /// <inheritdoc/>
        public void LogWarning(Exception exception, string message) => Log(LogLevel.Warning, exception, message);

        /// <inheritdoc/>
        public void LogWarning(string message, params object[] param) => Log(LogLevel.Warning, message, param);

        /// <inheritdoc/>
        public void LogWarning(Exception exception, string message, params object[] param) =>
            Log(LogLevel.Warning, exception, message, param);

        #endregion -- Logger helpers --

        /// <inheritdoc/>
        public void SetAttribute(string attribute, object? value)
        {
            if (value is not null)
            {
                RuntimeInfo.AppAttributes[attribute] = value;
            }
            else if (RuntimeInfo.AppAttributes.ContainsKey(attribute))
            {
                RuntimeInfo.AppAttributes.Remove(attribute);
            }

            UpdateRuntimeInformation();
        }

        /// <summary>
        ///     Updates runtime information
        /// </summary>
        /// <remarks>
        ///     Use a channel to make sure bad apps do not flood the
        ///     updating of
        /// </remarks>
        internal void UpdateRuntimeInformation() => _runtimeInfoManager?.UpdateRuntimeInformation();
    }
}
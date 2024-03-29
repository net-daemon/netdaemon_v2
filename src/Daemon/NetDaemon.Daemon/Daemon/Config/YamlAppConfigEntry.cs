﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using NetDaemon.Common;
using NetDaemon.Common.Exceptions;
using YamlDotNet.RepresentationModel;

namespace NetDaemon.Daemon.Config
{
    public class YamlAppConfigEntry
    {
        private readonly YamlMappingNode _yamlMappingNode;
        private readonly YamlConfigEntry _yamlConfigEntry;

        public string AppId { get; }

        public YamlAppConfigEntry(string appId,
                YamlMappingNode yamlMappingNode,
                YamlConfigEntry yamlConfigEntry)
        {
            AppId = appId;

            _yamlMappingNode = yamlMappingNode;
            _yamlConfigEntry = yamlConfigEntry;
        }

        public void SetPropertyConfig(ApplicationContext applicationContext)
        {
            if (applicationContext is null) throw new ArgumentNullException(nameof(applicationContext));

            var appInstance = applicationContext.ApplicationInstance ?? throw new InvalidOperationException("Application is not yet instantiated");

            foreach (KeyValuePair<YamlNode, YamlNode> entry in _yamlMappingNode.Children)
            {
                string? scalarPropertyName = ((YamlScalarNode)entry.Key).Value;
                try
                {
                    // Just continue to next configuration if null or class declaration
                    if (scalarPropertyName == null) continue;
                    if (scalarPropertyName == "class") continue;

                    var prop = appInstance.GetType().GetYamlProperty(scalarPropertyName) ??
                               throw new MissingMemberException($"{scalarPropertyName} is missing from the type {appInstance.GetType()}");

                    var instance = InstanceProperty(appInstance, prop.PropertyType, entry.Value, applicationContext);

                    prop.SetValue(appInstance, instance);
                }
                catch (Exception e)
                {
                    throw new NetDaemonException($"Failed to set value {scalarPropertyName} for app {applicationContext.Id}", e);
                }
            }
        }

        [SuppressMessage("", "CA1508")] // Weird bug that this should not warn!
        private object? InstanceProperty(object? parent, Type instanceType, YamlNode node, ApplicationContext applicationContext)
        {
            switch (node.NodeType)
            {
                case YamlNodeType.Scalar:
                {
                    var scalarNode = (YamlScalarNode) node;
                    ReplaceSecretIfExists(scalarNode);
                    return ((YamlScalarNode) node).ToObject(instanceType, applicationContext);
                }
                case YamlNodeType.Sequence when !instanceType.IsGenericType ||
                                                instanceType.GetGenericTypeDefinition() != typeof(IEnumerable<>):
                    return null;
                case YamlNodeType.Sequence:
                {
                    var list = CreateSequenceInstance(parent, instanceType, node, applicationContext);

                    return list;
                }
                case YamlNodeType.Mapping:
                {
                    var instance = CreateMappingInstance(instanceType, node, applicationContext);

                    return instance;
                }
                default:
                    return null;
            }
        }

        private object? CreateMappingInstance(Type instanceType, YamlNode node, ApplicationContext applicationContext)
        {
            var instance = Activator.CreateInstance(instanceType);

            foreach (KeyValuePair<YamlNode, YamlNode> entry in ((YamlMappingNode) node).Children)
            {
                var scalarPropertyName = ((YamlScalarNode) entry.Key).Value;
                // Just continue to next configuration if null or class declaration
                if (scalarPropertyName == null) continue;

                var childProp = instanceType.GetYamlProperty(scalarPropertyName) ??
                                throw new MissingMemberException($"{scalarPropertyName} is missing from the type {instanceType}");

                var valueType = entry.Value.NodeType;
                object? result = null;

                switch (valueType)
                {
                    case YamlNodeType.Sequence:
                        result = InstanceProperty(instance, childProp.PropertyType, (YamlSequenceNode) entry.Value, applicationContext);

                        break;

                    case YamlNodeType.Scalar:
                        result = InstanceProperty(instance, childProp.PropertyType, (YamlScalarNode) entry.Value, applicationContext);
                        break;

                    case YamlNodeType.Mapping:
                        result = CreateMappingInstance(childProp.PropertyType, entry.Value, applicationContext);
                        break;
                }

                childProp.SetValue(instance, result);
            }

            return instance;
        }

        [SuppressMessage("", "CA1508")]
        private IList CreateSequenceInstance(object? parent, Type instanceType, YamlNode node, ApplicationContext applicationContext)
        {
            Type listType = instanceType?.GetGenericArguments()[0] ??
                            throw new NetDaemonNullReferenceException(
                                $"The property {instanceType?.Name} of Class {parent?.GetType().Name} is not compatible with configuration");

            IList list = listType.CreateListOfPropertyType() ??
                         throw new NetDaemonNullReferenceException(
                                "Failed to create list type, please check {prop.Name} of Class {app.GetType().Name}");

            foreach (YamlNode item in ((YamlSequenceNode) node).Children)
            {
                var instance = InstanceProperty(null, listType, item, applicationContext) ??
                               throw new NotSupportedException($"The class {parent?.GetType().Name} has wrong type in items");

                list.Add(instance);
            }

            return list;
        }

        private void ReplaceSecretIfExists(YamlScalarNode scalarNode)
        {
            if (scalarNode.Tag != "!secret" && scalarNode.Value != null)
                return;

            var secretReplacement = _yamlConfigEntry.GetSecret(scalarNode.Value!);
            scalarNode.Value = secretReplacement ?? throw new NetDaemonException($"{scalarNode.Value!} not found in secrets.yaml");
        }
    }
}
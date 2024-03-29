﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NetDaemon.Common;
using NetDaemon.Common.Exceptions;
using NetDaemon.Daemon.Tests.DaemonRunner.App;
using Xunit;

namespace NetDaemon.Daemon.Tests.Daemon
{
     public class AppSorterTests
    {

        [Fact]
        public void SortEmptyTest()
        {
            AssertSortReturns(Array.Empty<ApplicationContext>(), Array.Empty<string>());
        }

        [Fact]
        public void SortOneTest()
        {
            var apps = new List<ApplicationContext>{ TestApp("a")};

            AssertSortReturns(apps, "a");
        }

        [Fact]
        public void SortSeveralAppsTest()
        {
            // ARRANGE
            var apps = new List<ApplicationContext>
            {
                TestApp("a", "b"),
                TestApp("b", "c"),
                TestApp("c"),
            };

            AssertSortReturns(apps, "c", "b", "a");
        }

        [Fact]
        public void SortSeveralApps2Test()
        {
            // ARRANGE
            var apps = new List<ApplicationContext>
            {
                TestApp("a", "c"),
                TestApp("b", "c", "a"),
                TestApp("c"),
            };

            AssertSortReturns(apps, "c", "a", "b");
        }

        static ApplicationContext TestApp(string id, params string[] dependencies)
        {
            var applicationContext = ApplicationContext.Create(typeof(AssemblyDaemonApp), id, new ServiceCollection().BuildServiceProvider());
            applicationContext.Dependencies = dependencies;
            return applicationContext;
        }

        [Fact]
        public void TrowsOnNotFoundDependency()
        {
            // ARRANGE
            var apps = new List<ApplicationContext>
            {
                TestApp("test","dependent_app")
            };

            // ACT
            Action act = ()=> AppSorter.SortByDependency(apps);

            // ASSERT
            var ex = Assert.Throws<NetDaemonException>(act);
            Assert.Contains("There is no app named dependent_app", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TrowsOnCycleTest()
        {
            // ARRANGE
            var apps = new List<ApplicationContext>
            {
                TestApp("a", "b"),
                TestApp("b", "a"),
            };
            // ACT
            Action act = ()=> AppSorter.SortByDependency(apps);

            // ASSERT
            var ex = Assert.Throws<NetDaemonException>(act);
            Assert.Contains("circular dependencies", ex.Message, StringComparison.Ordinal);
        }
      
        private static void AssertSortReturns(IReadOnlyList<ApplicationContext> apps, params string[] expected)
        {
            // ACT
            var sorted = AppSorter.SortByDependency(apps);

            // ASSERT
            Assert.Equal(expected, sorted.Select(a => a.Id));
      
            var sortedReverse = AppSorter.SortByDependency(apps.Reverse().ToArray());
            Assert.Equal(expected, sortedReverse.Select(a => a.Id));
        }
    }
}
// Copyright (C) Funplay. Licensed under MIT.

using Funplay.Editor.Services.UnityLogs;
using NUnit.Framework;
using UnityEngine;

namespace Funplay.Editor.Tests
{
    public sealed class UnityLogsRepositoryTests
    {
        [Test]
        public void GetRecentLogs_FiltersGroupsAndTruncatesCachedEntries()
        {
            var token = "FunplayConsoleGrouping_" + System.Guid.NewGuid().ToString("N");
            var longPayload = new string('x', 360);

            using (var repository = new UnityLogsRepository())
            {
                repository.StartListening();
                repository.Clear();

                Debug.Log(token + " duplicate");
                Debug.Log(token + " duplicate");
                Debug.Log(token + " unique");
                Debug.Log(token + " " + longPayload);

                var grouped = repository.GetRecentLogs(
                    logType: "log",
                    count: 10,
                    sinceSeconds: 0,
                    filterText: token,
                    groupDuplicates: true);

                Assert.That(grouped, Does.Contain("Console logs (4 entries, 3 unique, filter: log, source: cache"));
                Assert.That(grouped, Does.Contain("[LOG] " + token + " duplicate (x2)"));
                Assert.That(grouped, Does.Contain("[LOG] " + token + " unique"));
                Assert.That(grouped, Does.Contain("... (+"));
                Assert.That(grouped.Length, Is.LessThan(1400));

                var filtered = repository.GetRecentLogs(
                    logType: "log",
                    count: 10,
                    sinceSeconds: 0,
                    filterText: token + " unique",
                    groupDuplicates: true);

                Assert.That(filtered, Does.Contain("Console logs (1 entries, filter: log, source: cache"));
                Assert.That(filtered, Does.Contain("[LOG] " + token + " unique"));
                Assert.That(filtered, Does.Not.Contain("duplicate"));

                var ungrouped = repository.GetRecentLogs(
                    logType: "log",
                    count: 10,
                    sinceSeconds: 0,
                    filterText: token + " duplicate",
                    groupDuplicates: false);

                Assert.That(ungrouped, Does.Contain("Console logs (2 entries, filter: log, source: cache"));
                Assert.That(ungrouped, Does.Not.Contain("(x2)"));
            }
        }

        [Test]
        public void HelperMethods_HandleEmptyTextAndLongLines()
        {
            Assert.IsTrue(UnityLogsRepository.MatchesTextFilter("Hello Console", "console"));
            Assert.IsFalse(UnityLogsRepository.MatchesTextFilter("Hello Console", "missing"));
            Assert.IsTrue(UnityLogsRepository.MatchesTextFilter(null, null));
            Assert.IsFalse(UnityLogsRepository.MatchesTextFilter(null, "missing"));

            var line = new string('a', 305);
            var truncated = UnityLogsRepository.TruncateLine(line);

            Assert.That(truncated, Does.StartWith(new string('a', 300)));
            Assert.That(truncated, Does.EndWith("... (+5 chars)"));
        }
    }
}

// Copyright (C) Funplay. Licensed under MIT.

using System;
using Funplay.Editor.Tools.Builtins;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class TestRunnerFunctionsTests
    {
        private static readonly DateTime Now = new DateTime(2026, 7, 13, 12, 0, 0);

        [Test]
        public void StuckAssessment_FlagsRunnerStartAtThirtySeconds()
        {
            Assert.IsNull(TestRunnerFunctions.AssessStuckState(Now.AddSeconds(-29), null, Now));

            var assessment = TestRunnerFunctions.AssessStuckState(Now.AddSeconds(-30), null, Now);

            Assert.IsNotNull(assessment);
            Assert.AreEqual("runner_start_or_transition", assessment.Phase);
            Assert.AreEqual(30, assessment.SecondsSinceActivity);
            StringAssert.Contains("another caller", assessment.Hint);
        }

        [Test]
        public void StuckAssessment_GivesKnownTestLongerThreshold()
        {
            const string testName = "Game.Tests.LoadsLargeScene";
            Assert.IsNull(TestRunnerFunctions.AssessStuckState(Now.AddSeconds(-60), testName, Now));
            Assert.IsNull(TestRunnerFunctions.AssessStuckState(Now.AddSeconds(-119), testName, Now));

            var assessment = TestRunnerFunctions.AssessStuckState(Now.AddSeconds(-120), testName, Now);

            Assert.IsNotNull(assessment);
            Assert.AreEqual("test_execution", assessment.Phase);
            Assert.AreEqual(120, assessment.SecondsSinceActivity);
            StringAssert.Contains(testName, assessment.Hint);
            StringAssert.Contains("legitimate long-running test", assessment.Hint);
        }

        [Test]
        public void StuckAssessment_IgnoresFutureActivity()
        {
            Assert.IsNull(TestRunnerFunctions.AssessStuckState(Now.AddSeconds(60), null, Now));
        }
    }
}

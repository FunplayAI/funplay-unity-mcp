// Copyright (C) GameBooom. Licensed under GPLv3.

using System;
using UnityEditor;
using UnityEngine;

namespace GameBooom.Editor.State
{
    /// <summary>
    /// Saves and restores running state across Unity domain reloads (triggered by script recompilation).
    /// Uses SessionState (persists within editor session, cleared on editor restart).
    /// </summary>
    internal static class DomainReloadHandler
    {
        private const string StateKey = "GameBooom_ReloadState";
        private const string TimestampKey = "GameBooom_ReloadTimestamp";
        private const string ResumeCountKey = "GameBooom_ConsecutiveResumeCount";
        private const string LastResumeTimestampKey = "GameBooom_LastResumeTimestamp";

        private const int MaxConsecutiveResumes = 5;
        private const double ResumeCountResetSeconds = 120;

        private static bool _registered;

        /// <summary>
        /// Register to receive reload events. Call once (idempotent).
        /// </summary>
        public static void Register(IStateController stateController)
        {
            if (_registered) return;
            _registered = true;

            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                SaveState(stateController.CurrentState);
            };
        }

        public static void SaveState(GameBooomState state)
        {
            SessionState.SetString(StateKey, state.ToString());
            SessionState.SetString(TimestampKey, DateTime.Now.ToString("O"));
        }

        /// <summary>
        /// Checks whether auto-resume is allowed based on the consecutive resume counter.
        /// </summary>
        public static bool CanAutoResume()
        {
            var count = SessionState.GetInt(ResumeCountKey, 0);
            var lastTimestampStr = SessionState.GetString(LastResumeTimestampKey, "");

            if (DateTime.TryParse(lastTimestampStr, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var lastTs))
            {
                if ((DateTime.Now - lastTs).TotalSeconds > ResumeCountResetSeconds)
                    count = 0;
            }

            return count < MaxConsecutiveResumes;
        }

        public static void RecordAutoResume()
        {
            var count = SessionState.GetInt(ResumeCountKey, 0);
            var lastTimestampStr = SessionState.GetString(LastResumeTimestampKey, "");

            if (DateTime.TryParse(lastTimestampStr, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var lastTs))
            {
                if ((DateTime.Now - lastTs).TotalSeconds > ResumeCountResetSeconds)
                    count = 0;
            }

            count++;
            SessionState.SetInt(ResumeCountKey, count);
            SessionState.SetString(LastResumeTimestampKey, DateTime.Now.ToString("O"));
        }

        public static void ResetResumeCounter()
        {
            SessionState.SetInt(ResumeCountKey, 0);
            SessionState.EraseString(LastResumeTimestampKey);
        }

        /// <summary>
        /// Checks if there was an interrupted operation before the last domain reload.
        /// Returns the state that was active, or null if nothing was running.
        /// Clears the saved state after reading (one-shot).
        /// </summary>
        public static InterruptedState ConsumeInterruptedState()
        {
            var stateStr = SessionState.GetString(StateKey, "");
            var timestampStr = SessionState.GetString(TimestampKey, "");

            // Clear after reading
            SessionState.EraseString(StateKey);
            SessionState.EraseString(TimestampKey);

            if (string.IsNullOrEmpty(stateStr)) return null;

            if (!Enum.TryParse<GameBooomState>(stateStr, out var state))
                return null;

            if (state == GameBooomState.Initialized)
                return null;

            // Discard if too old (> 120 seconds)
            if (DateTime.TryParse(timestampStr, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            {
                if ((DateTime.Now - ts).TotalSeconds > 120)
                    return null;
            }

            return new InterruptedState
            {
                State = state,
                Timestamp = ts
            };
        }

        internal class InterruptedState
        {
            public GameBooomState State;
            public DateTime Timestamp;

            public string GetDescription()
            {
                switch (State)
                {
                    case GameBooomState.ExecutingFunction:
                    case GameBooomState.ExecutingAllFunctions:
                        return "Function execution was interrupted by script recompilation.";
                    default:
                        return "Operation was interrupted by script recompilation.";
                }
            }
        }
    }
}

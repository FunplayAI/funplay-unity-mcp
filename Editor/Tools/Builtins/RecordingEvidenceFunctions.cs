// Copyright (C) Funplay. Licensed under MIT.
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using Funplay.Editor.State;
namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Video")]
    internal static class RecordingEvidenceFunctions
    {
        [Description("Extract 1..16 PNG frames from a finalized recording with Unity's decoder. start takes recording_id and JSON timestamps; MCP briefly waits, then get_task reads progress/results. Returns requested/actual times and paths. Legacy status/cancel/cleanup take job_id; cleanup deletes only generated PNGs, never video. Reload interrupts extraction; times are sorted/deduplicated.")]
        public static object ExtractRecordingFrames(
            [ToolParam("start, status, cancel or cleanup", Required = false)] string action = "start",
            [ToolParam("Finalized recording ID for start", Required = false)] string recording_id = null,
            [ToolParam("JSON timestamps, e.g. [0,1.5,3]", Required = false)] string timestamps = null,
            [ToolParam("Extraction job ID for status/cancel/cleanup", Required = false)] string job_id = null,
            [ToolParam("MCP completion wait, 0..30 seconds", Required = false)] int wait_seconds = 2)
            => TaskStatusService.ValidateWait(wait_seconds) ?? TaskStatusService.Attach("recording_frames",
                action == "start" ? RecordingEvidenceService.Start(recording_id, timestamps) : RecordingEvidenceService.Control(job_id, action));

        [Description("Return one extracted video frame as a native MCP image plus timestamp/geometry metadata, or path-only. Historical frames are not valid live click captures. Index is in the extraction job's sorted timestamp list.")]
        [ReadOnlyTool]
        public static object GetRecordingFrame(
            [ToolParam("Extraction job ID")] string job_id,
            [ToolParam("Zero-based frame index")] int index,
            [ToolParam("Include native image; false returns file receipt only", Required = false)] bool inline = true)
            => RecordingEvidenceService.ReadFrame(job_id, index, inline);
    }
}

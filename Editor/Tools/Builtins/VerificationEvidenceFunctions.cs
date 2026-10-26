// Copyright (C) Funplay. Licensed under MIT.
using System.ComponentModel;
using Funplay.Editor.Tools.Helpers;
namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Verification")]
    internal static class VerificationEvidenceFunctions
    {
        [Description("Persist bounded static resource inventory and caller-authored continuation notes under Library. Fingerprint file/meta/dependency hashes, Unity version and configuration. Reports missing-script and unresolved-GUID counts separately. No scene is opened or saved. Historical checkpoints never certify current runtime state. Full capability.")]
        public static object SaveVerificationEvidence(
            [ToolParam("JSON array of 1..200 explicit Assets/ProjectSettings files or package manifests")] string paths,
            [ToolParam("JSON continuation notes: next step, blockers, unverified acceptance layers")] string checkpoint,
            [ToolParam("Current relevant task configuration as JSON", Required = false)] string configuration = null)
            => VerificationEvidenceStore.Save(paths, configuration, checkpoint);

        [Description("Read a persisted checkpoint/static evidence receipt and revalidate its exact file/meta/import-dependency/configuration hashes. Read-only; never resumes, cleans up, switches scenes or reuses cached runtime observations. cache_valid applies only to static inputs. Full capability.")]
        [ReadOnlyTool]
        public static object ReadVerificationEvidence(
            [ToolParam("Evidence ID from save")] string evidence_id,
            [ToolParam("Current task configuration if different from the saved configuration", Required = false)] string configuration = null)
            => VerificationEvidenceStore.Read(evidence_id, configuration);
    }
}

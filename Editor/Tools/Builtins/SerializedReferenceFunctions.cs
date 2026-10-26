// Copyright (C) Funplay. Licensed under MIT.
using System.ComponentModel;
using Funplay.Editor.Tools.Helpers;
namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("References")]
    internal static class SerializedReferenceFunctions
    {
        [Description("Guard one persistent .asset/.prefab ObjectReference edit with expected SHA256 and old GUID/fileID. Preflight a disposable Unity-created copy, require all untouched raw PPtr slots to survive, save/import/read back the live asset, verify or byte-restore backup on failure. Rejects binary/ambiguous/dirty assets. No YAML hand editing, unresolved-reference clearing or broad SaveAssets. Full capability; callback side effects are not transactional.")]
        public static object PatchSerializedReference(
            [ToolParam("Normalized Assets .asset/.prefab path")] string asset_path,
            [ToolParam("Persistent serialized object's local file ID")] long local_file_id,
            [ToolParam("Exact SerializedProperty path, including Array.data[index] when needed")] string property,
            [ToolParam("SHA256 of current asset file bytes")] string expected_sha256,
            [ToolParam("Expected old reference GUID, empty for local/null refs")] string expected_guid,
            [ToolParam("Expected old reference fileID")] long expected_file_id,
            [ToolParam("Persistent replacement path; omit to set null", Required = false)] string replacement_asset = null,
            [ToolParam("Replacement subasset's exact local file ID", Required = false)] long replacement_local_file_id = 0)
            => SerializedReferenceTransaction.Patch(asset_path, local_file_id, property, expected_sha256, expected_guid, expected_file_id, replacement_asset, replacement_local_file_id);
    }
}

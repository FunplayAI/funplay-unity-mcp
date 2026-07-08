// Copyright (C) Funplay. Licensed under MIT.
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Undo")]
    internal static class UndoFunctions
    {
        [Description("Perform a single Undo step. NOTE: operates on the shared Editor's global undo stack — this reverts whatever was last done in the Editor, including the user's own actions; use with care.")]
        [SceneEditingTool]
        public static object Undo()
        {
            UnityEditor.Undo.PerformUndo();
            return Response.Success("Performed undo.", new
            {
                performed = true,
                currentGroupName = UnityEditor.Undo.GetCurrentGroupName()
            });
        }

        [Description("Perform a single Redo step. NOTE: operates on the shared Editor's global undo stack — this reapplies whatever was last undone in the Editor, including the user's own actions; use with care.")]
        [SceneEditingTool]
        public static object Redo()
        {
            UnityEditor.Undo.PerformRedo();
            return Response.Success("Performed redo.", new
            {
                performed = true,
                currentGroupName = UnityEditor.Undo.GetCurrentGroupName()
            });
        }

        [Description("Read the current state of the Editor's undo stack (top group name and index). NOTE: this reflects the shared Editor's global undo stack, which includes whatever the user last did too; use with care.")]
        [ReadOnlyTool]
        public static object GetUndoState()
        {
            return Response.Success("Current undo state.", new
            {
                currentGroupName = UnityEditor.Undo.GetCurrentGroupName(),
                currentGroup = UnityEditor.Undo.GetCurrentGroup()
            });
        }
    }
}

// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Funplay.Editor.State
{
    // Use the same guarded file I/O as execution receipts without changing the
    // prepare_editor journal format or re-running any Editor operation.
    internal sealed class EditorOperationJournal
    {
        private readonly string path;
        private readonly Action<string, string> replaceFile;
        private string persistedHash;
        private bool loaded;

        internal EditorOperationJournal(string path, Action<string, string> replace = null)
        {
            this.path = Path.GetFullPath(path);
            replaceFile = replace;
        }

        internal List<EditorOperation> Load()
        {
            loaded = false;
            byte[] bytes = File.Exists(path) ? ExecutionAtomicFile.Read(path) : null;
            var operations = new List<EditorOperation>();
            if (bytes != null)
            {
                using (var input = new MemoryStream(bytes))
                using (var reader = new StreamReader(input, detectEncodingFromByteOrderMarks: true))
                    operations = JsonConvert.DeserializeObject<List<EditorOperation>>(reader.ReadToEnd()) ?? operations;
                if (!EditorOperationCoordinator.ValidJournal(operations))
                    throw new InvalidDataException("Invalid operation journal; no actions will be replayed.");
            }
            persistedHash = bytes == null ? null : ExecutionJobStore.HashBytes(bytes);
            loaded = true;
            return operations;
        }

        internal void Save(List<EditorOperation> operations)
        {
            if (!loaded) throw new InvalidDataException("Operation journal was not loaded successfully.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var text = JsonConvert.SerializeObject(operations, Formatting.Indented);
            ExecutionAtomicFile.Write(path, text, "editor_operation_journal", persistedHash, replaceFile);
            persistedHash = ExecutionJobStore.Hash(text);
        }
    }
}

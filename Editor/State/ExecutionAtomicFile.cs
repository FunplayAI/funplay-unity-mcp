// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Funplay.Editor.State
{
    internal sealed class ExecutionStorageException : IOException
    {
        internal readonly string Operation, Role;
        internal readonly int Attempts;
        internal ExecutionStorageException(string operation, string role, int attempts, Exception cause)
            : base("Execution storage operation failed; original files were not deleted.", cause)
        { Operation = operation; Role = role; Attempts = attempts; HResult = cause.HResult; }
    }

    internal static class ExecutionAtomicFile
    {
        internal const int MaxJournalBytes = 8 * 1024 * 1024;
        internal static byte[] Read(string path, int limit = MaxJournalBytes)
        {
            // Allow atomic replacement by our writer on Windows; never hold a read handle
            // without FileShare.Delete and accidentally cause our own sharing violation.
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var bytes = new MemoryStream())
            {
                if (file.Length > limit) throw new InvalidDataException("Oversized execution file.");
                var buffer = new byte[8192]; int count;
                while ((count = file.Read(buffer, 0, Math.Min(buffer.Length, limit + 1 - (int)bytes.Length))) > 0)
                { bytes.Write(buffer, 0, count); if (bytes.Length > limit) throw new InvalidDataException("Oversized execution file."); }
                return bytes.ToArray();
            }
        }
        internal static void Check(string path, string expectedHash)
        {
            if (expectedHash == null)
            { if (File.Exists(path)) throw new InvalidDataException("Execution file appeared unexpectedly; preserved for review."); }
            else if (!File.Exists(path) || ExecutionJobStore.HashBytes(Read(path)) != expectedHash)
                throw new InvalidDataException("Execution file changed or disappeared; preserved for review.");
        }
        internal static void Write(string path, string text, string role, string expectedHash,
            Action<string, string> replace = null)
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            if (bytes.Length > MaxJournalBytes) throw new InvalidDataException("Execution file exceeds the storage limit.");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string operation = "write_temporary"; int attempts = 0;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                operation = expectedHash == null ? "move_new" : "replace_existing";
                while (true)
                {
                    Check(path, expectedHash); attempts++;
                    try
                    {
                        if (expectedHash == null) File.Move(temporary, path);
                        else if (replace != null) replace(temporary, path);
                        else File.Replace(temporary, path, null);
                        return;
                    }
                    catch (IOException ex) when (TransientSharingViolation(ex) && attempts < 3)
                    { Thread.Sleep(attempts * 20); }
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            { throw new ExecutionStorageException(operation, role, attempts, ex); }
            finally
            {
                // This exact GUID path was created by this call. Cleanup failure must not mask
                // the primary diagnostic or turn a committed atomic replacement into failure.
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        private static bool TransientSharingViolation(IOException error) =>
            (error.HResult & unchecked((int)0xffff0000)) == unchecked((int)0x80070000) &&
            ((error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33);
    }
}

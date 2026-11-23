// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Funplay.Editor.State
{
    internal sealed class ExecutionStorageException : IOException
    {
        internal readonly string Operation, Role;
        internal readonly int Attempts;
        internal ExecutionStorageException(string operation, string role, int attempts, Exception cause)
            : base("Atomic storage write failed; inspect the journal diagnostic before recovering.", cause)
        { Operation = operation; Role = role; Attempts = attempts; HResult = cause.HResult; }
    }

    internal static class ExecutionAtomicFile
    {
        internal const int MaxJournalBytes = 8 * 1024 * 1024;
        private const int MaxAttempts = 3;
        private const int SharingViolation = 32;
        private const int LockViolation = 33;
        private const int UnableToRemoveReplaced = 1175;
        private const int UnableToMoveReplacement = 1176;
        private const int UnableToMoveReplacementWithChangedState = 1177;
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
            bool ownsTemporary = false, preserveTemporary = false;
            bool replacing = expectedHash != null;
            string temporaryHash = null;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporary = true;
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                operation = replacing ? "replace_existing" : "move_new";
                while (true)
                {
                    try
                    {
                        if (attempts > 0) Check(temporary, temporaryHash);
                        Check(path, expectedHash);
                    }
                    catch when (attempts > 0)
                    {
                        // An uncertain state after replacement is not safe to overwrite.
                        // Keep any remaining staged evidence for explicit operator review.
                        preserveTemporary = true;
                        throw;
                    }
                    attempts++;
                    try
                    {
                        if (!replacing) File.Move(temporary, path);
                        else if (replace != null) replace(temporary, path);
                        else File.Replace(temporary, path, null);
                        return;
                    }
                    catch (IOException ex)
                    {
                        if (replacing && ReplacementVerified(temporary, path, bytes)) return;
                        int code = Win32ErrorCode(ex);
                        // ReplaceFileW documents partial file-name changes for 1176/1177.
                        // Keep any remaining staged bytes for review; never try to reinstall them.
                        if (replacing && (code == UnableToMoveReplacement || code == UnableToMoveReplacementWithChangedState))
                            preserveTemporary = true;
                        if (attempts >= MaxAttempts ||
                            !(code == SharingViolation || code == LockViolation || replacing && code == UnableToRemoveReplaced))
                            throw;
                        // For 1175, Windows documents both names as retained. Verify the actual
                        // bytes of BOTH files after backoff, before issuing another replacement.
                        temporaryHash = temporaryHash ?? ExecutionJobStore.HashBytes(bytes);
                        Thread.Sleep(attempts * 20);
                    }
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            { throw new ExecutionStorageException(operation, role, attempts, ex); }
            finally
            {
                // Never delete the destination or an unowned temporary. A partial native
                // replacement may have left this temporary as the only staged copy.
                if (ownsTemporary && !preserveTemporary)
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static bool ReplacementVerified(string temporary, string path, byte[] intended)
        {
            // File.Exists(false) includes access/I/O failures. Require positive absence of
            // the staged file before accepting the exact intended bytes at the destination.
            try { File.GetAttributes(temporary); return false; }
            catch (FileNotFoundException) { }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }

            try { return intended.SequenceEqual(Read(path, intended.Length)); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            { return false; }
        }

        private static int Win32ErrorCode(IOException error) =>
            (error.HResult & unchecked((int)0xffff0000)) == unchecked((int)0x80070000) ? error.HResult & 0xffff : -1;
    }
}

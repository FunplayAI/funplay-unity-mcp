// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using Funplay.Editor.Tools.Builtins;
using NUnit.Framework;
using Unity.Profiling.Memory;

namespace Funplay.Editor.Tests
{
    public sealed class MemorySnapshotFunctionsTests
    {
        [Test]
        public void TryParseCaptureFlags_DefaultsToManagedAndNativeObjects()
        {
            Assert.IsTrue(MemorySnapshotFunctions.TryParseCaptureFlags(null, out var flags, out var error));

            Assert.IsNull(error);
            Assert.AreEqual(CaptureFlags.ManagedObjects | CaptureFlags.NativeObjects, flags);
        }

        [Test]
        public void TryParseCaptureFlags_AcceptsCommaSeparatedFlags()
        {
            Assert.IsTrue(MemorySnapshotFunctions.TryParseCaptureFlags(
                "ManagedObjects, NativeAllocations",
                out var flags,
                out var error));

            Assert.IsNull(error);
            Assert.IsTrue((flags & CaptureFlags.ManagedObjects) != 0);
            Assert.IsTrue((flags & CaptureFlags.NativeAllocations) != 0);
        }

        [Test]
        public void TryParseCaptureFlags_RejectsUnknownFlag()
        {
            Assert.IsFalse(MemorySnapshotFunctions.TryParseCaptureFlags(
                "ManagedObjects,DefinitelyNotAFlag",
                out _,
                out var error));

            Assert.IsNotNull(error);
        }

        [Test]
        public void TryResolveSnapshotPath_AcceptsExistingRelativeNameWithoutExtension()
        {
            var storageDir = MakeTempStorageDir();
            var expectedPath = Path.Combine(storageDir, "capture.snap");
            Directory.CreateDirectory(storageDir);
            File.WriteAllText(expectedPath, "not a real snapshot");

            Assert.IsTrue(MemorySnapshotFunctions.TryResolveSnapshotPath(
                "capture",
                storageDir,
                out var path,
                out var error));

            Assert.IsNull(error);
            Assert.AreEqual(Path.GetFullPath(expectedPath), path);
        }

        [Test]
        public void TryResolveSnapshotPath_AcceptsExistingAbsoluteSnapPath()
        {
            var storageDir = MakeTempStorageDir();
            var expectedPath = Path.Combine(storageDir, "absolute.snap");
            Directory.CreateDirectory(storageDir);
            File.WriteAllText(expectedPath, "not a real snapshot");

            Assert.IsTrue(MemorySnapshotFunctions.TryResolveSnapshotPath(
                expectedPath,
                storageDir,
                out var path,
                out var error));

            Assert.IsNull(error);
            Assert.AreEqual(Path.GetFullPath(expectedPath), path);
        }

        [Test]
        public void TryResolveSnapshotPath_RejectsEmptySnapshot()
        {
            Assert.IsFalse(MemorySnapshotFunctions.TryResolveSnapshotPath(
                "",
                MakeTempStorageDir(),
                out var path,
                out var error));

            Assert.IsNull(path);
            Assert.IsNotNull(error);
        }

        [Test]
        public void TryResolveSnapshotPath_RejectsTraversalOutsideStorageDirectory()
        {
            Assert.IsFalse(MemorySnapshotFunctions.TryResolveSnapshotPath(
                "../outside.snap",
                MakeTempStorageDir(),
                out var path,
                out var error));

            Assert.IsNull(path);
            Assert.IsNotNull(error);
        }

        [Test]
        public void TryResolveSnapshotPath_RejectsAbsolutePathWithoutSnapExtension()
        {
            var storageDir = MakeTempStorageDir();
            var notSnap = Path.Combine(storageDir, "not-a-snapshot.txt");
            Directory.CreateDirectory(storageDir);
            File.WriteAllText(notSnap, "not a snapshot");

            Assert.IsFalse(MemorySnapshotFunctions.TryResolveSnapshotPath(
                notSnap,
                storageDir,
                out var path,
                out var error));

            Assert.IsNull(path);
            Assert.IsNotNull(error);
        }

        private static string MakeTempStorageDir()
        {
            return Path.Combine(Path.GetTempPath(), "FunplayMcpMemorySnapshotTests", Guid.NewGuid().ToString("N"));
        }
    }
}

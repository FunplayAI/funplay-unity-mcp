// Copyright (C) Funplay. Licensed under MIT.

using System.Threading.Tasks;
using Funplay.Editor.Threading;
using NUnit.Framework;

namespace Funplay.Editor
{
    public sealed class EditorThreadHelperLifecycleTests
    {
        [Test]
        public void ExecuteAsyncOnEditorThreadAsync_CancelsQueuedOuterTaskWhenDisposed()
        {
            var helper = new EditorThreadHelper(null);
            Task<int> queuedTask = null;

            Task.Run(() =>
            {
                queuedTask = helper.ExecuteAsyncOnEditorThreadAsync(async () =>
                {
                    await Task.Yield();
                    return 42;
                });
            }).Wait();

            helper.Dispose();

            Assert.IsNotNull(queuedTask);
            Assert.IsTrue(queuedTask.IsCanceled);
        }

        [Test]
        public void ExecuteAsyncOnEditorThreadAsync_RejectsNewWorkAfterDispose()
        {
            var helper = new EditorThreadHelper(null);
            helper.Dispose();

            var rejectedTask = helper.ExecuteAsyncOnEditorThreadAsync(() => Task.FromResult(42));

            Assert.IsTrue(rejectedTask.IsCanceled);
        }
    }
}

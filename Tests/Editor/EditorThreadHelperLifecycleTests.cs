// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Threading.Tasks;
using Funplay.Editor.MCP.Server;
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
        public void DisposedPumpRaisesTheCancellationTheServerTreatsAsShutdown()
        {
            // Ties the pump's dispose-cancellation to the request handler's classification: awaiting
            // the canceled task raises TaskCanceledException ("A task was canceled."), which used to
            // be logged as "Error handling request" on every domain reload that caught a queued request.
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

            Exception observed = null;
            try
            {
                queuedTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                observed = ex;
            }

            Assert.IsInstanceOf<TaskCanceledException>(observed);
            Assert.IsTrue(MCPServerService.IsShutdownCancellation(observed));
        }

        [Test]
        public void ExecuteOnEditorThreadAsync_CancelsQueuedGenericTaskWhenDisposed()
        {
            var helper = new EditorThreadHelper(null);
            Task<int> queuedTask = null;

            Task.Run(() =>
            {
                queuedTask = helper.ExecuteOnEditorThreadAsync(() => 42);
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

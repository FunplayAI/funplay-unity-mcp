// Copyright (C) Funplay. Licensed under MIT.

using Funplay.Editor.MCP.Server;
using NUnit.Framework;

namespace Funplay.Editor.Tests
{
    public sealed class MCPToolListChangeNotifierTests
    {
        [Test]
        public void TryConsumePending_ConsumesOnceUntilRestored()
        {
            MCPToolListChangeNotifier.RestorePending();

            Assert.IsTrue(MCPToolListChangeNotifier.TryConsumePending());
            Assert.IsFalse(MCPToolListChangeNotifier.TryConsumePending());

            MCPToolListChangeNotifier.RestorePending();

            Assert.IsTrue(MCPToolListChangeNotifier.TryConsumePending());
            Assert.IsFalse(MCPToolListChangeNotifier.TryConsumePending());
        }

        [Test]
        public void BuildSseBody_EmitsNotificationBeforeResponse()
        {
            var body = MCPToolListChangeNotifier.BuildSseBody("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{}}");

            Assert.That(body, Does.StartWith("data: " + MCPToolListChangeNotifier.NotificationJson + "\n\n"));
            Assert.That(body, Does.Contain("data: {\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{}}\n\n"));
        }
    }
}

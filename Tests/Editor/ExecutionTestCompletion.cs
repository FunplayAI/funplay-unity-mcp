// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Threading.Tasks;
using Funplay.Editor.State;
using Newtonsoft.Json.Linq;

namespace Funplay.Editor.Tests
{
    // Test callers must observe a returned task instead of assuming a two-second receipt is
    // completion. This helper never replays code and does not pump Unity/input manually.
    internal static class ExecutionTestCompletion
    {
        internal static async Task<object> Finish(Task<object> start)
        {
            var result = await start.ConfigureAwait(false);
            var json = result as JObject ?? JObject.FromObject(result);
            var id = (string)json["data"]?["job_id"];
            if (id == null) return result;
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (ExecutionJobService.Store.Find(id)?.Terminal == false)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Owned execution fixture did not finish.");
                await Task.Delay(10).ConfigureAwait(false);
            }
            return ExecutionJobService.Store.Read(id, null, originalResult: true);
        }
    }
}

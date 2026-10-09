using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace StrataHome
{
    internal static class UpdaterTest
    {
        public static int Run()
        {
            int failures = 0;
            DateTime now = DateTime.UtcNow;
            double stamp = (now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            Func<string, double, object, object> metrics = delegate(string state, double time, object queued)
            {
                return new Dictionary<string, object> { { "time", time }, { "live", new Dictionary<string, object> { { "state", state }, { "queued", queued } } } };
            };
            Action<string, bool> check = delegate(string name, bool ok)
            {
                File.AppendAllText(Path.Combine(Paths.Logs, "updater-test.txt"), (ok ? "PASS " : "FAIL ") + name + "\r\n");
                if (!ok) failures++;
            };
            Directory.CreateDirectory(Paths.Logs);
            File.WriteAllText(Path.Combine(Paths.Logs, "updater-test.txt"), "");
            check("fresh idle sample can update", StrataUpdater.IdleMetrics(metrics("idle", stamp, 0), now));
            check("unloaded server can update", StrataUpdater.IdleMetrics(metrics("unloaded", stamp, 0), now));
            check("generating defers update", !StrataUpdater.IdleMetrics(metrics("generating", stamp, 0), now));
            check("reading defers update", !StrataUpdater.IdleMetrics(metrics("reading", stamp, 0), now));
            check("queued request defers update", !StrataUpdater.IdleMetrics(metrics("idle", stamp, 1), now));
            check("stale sample defers update", !StrataUpdater.IdleMetrics(metrics("idle", stamp - 20, 0), now));
            check("unknown state defers update", !StrataUpdater.IdleMetrics(metrics("loading", stamp, 0), now));
            check("missing sample defers update", !StrataUpdater.IdleMetrics(null, now));
            check("missing queue field defers update", !StrataUpdater.IdleMetrics(metrics("idle", stamp, null), now));
            check("future timestamp defers update", !StrataUpdater.IdleMetrics(metrics("idle", stamp + 30, 0), now));
            check("Windows trailing slash quoted", StrataUpdater.Quote("C:\\a b\\") == "\"C:\\a b\\\\\"");
            check("Windows quote escaped", StrataUpdater.Quote("a\"b") == "\"a\\\"b\"");
            return failures == 0 ? 0 : 1;
        }
    }
}

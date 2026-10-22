using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AIUsageTray
{
    internal static class T7Tests
    {
        private static int passed;
        private static string root;
        private static string pidFile;
        private static DateTimeOffset now;

        private static int Main()
        {
            root = Environment.GetEnvironmentVariable("T7_ROOT");
            pidFile = Environment.GetEnvironmentVariable("T7_PID_FILE");
            long capturedNow;
            string captured = Environment.GetEnvironmentVariable("T7_NOW");
            if (String.IsNullOrEmpty(root) || String.IsNullOrEmpty(pidFile) ||
                !Int64.TryParse(captured, out capturedNow))
            {
                Console.WriteLine("FAIL: T7_ROOT, T7_NOW and T7_PID_FILE are required");
                return 2;
            }
            now = DateTimeOffset.FromUnixTimeMilliseconds(capturedNow);
            root = Path.GetFullPath(root);
            try
            {
                CheckDatabasePath();
                string before = HashFile(OpenCodeFiles.DatabasePath());
                Limits();
                Basic();
                Partial();
                Duplicate();
                FreeLimitKinds();
                FreeLimitWithoutModel();
                Aborted();
                GenericErrors();
                RetryAfterCases();
                RetrySnapshots();
                UtcBoundary();
                ReaderBasic();
                ReaderConcurrent();
                ReaderTimeout();
                ReaderCancel();
                ReaderOrphan();
                Check(HashFile(OpenCodeFiles.DatabasePath()) == before, "temporary database changed during reads");
                Console.WriteLine("PASS: " + passed + " T7 checks");
                return 0;
            }
            catch (Exception error)
            {
                Console.WriteLine("FAIL: " + error.Message);
                return 1;
            }
        }

        private static void Limits()
        {
            Check(AppLimits.RefreshMilliseconds == 60000, "refresh interval=" + AppLimits.RefreshMilliseconds);
            Check(AppLimits.ReadTimeoutMilliseconds == 15000, "read timeout=" + AppLimits.ReadTimeoutMilliseconds);
            Check(AppLimits.ExitGraceMilliseconds == 3000, "exit grace=" + AppLimits.ExitGraceMilliseconds);
        }

        private static void CheckDatabasePath()
        {
            string path = OpenCodeFiles.DatabasePath();
            Check(!String.IsNullOrEmpty(path), "OpenCode database path is empty");
            path = Path.GetFullPath(path);
            Check(path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "OpenCode database resolved outside T7_ROOT: " + path);
            Check(File.Exists(path), "temporary OpenCode database is missing: " + path);
        }

        private static void Basic()
        {
            DateTimeOffset now = Utc(2026, 10, 21, 12, 0);
            List<object> rows = new List<object> {
                Step("m1", now.AddMinutes(-20), 100, 20, 5, 10, 2, 0.5, "deepseek-v4.1-flash-free"),
                Step("m2", now.AddMinutes(-10), 50, 10, 0, 0, 0, 0.25, "big-pickle"),
                Error("m3", now.AddMinutes(-5), "FreeUsageLimitError", "Free usage exceeded", null, null, "opencode", "deepseek-v4.1-flash-free"),
                Step("m4", now.AddMinutes(-4), 1, 1, 1, 1, 1, 0.01, "deepseek-v4.1-flash-free"),
                Error("m4", now.AddMinutes(-3), "FreeUsageLimitError", "free limit reached", null, null, "opencode", "deepseek-v4.1-flash-free"),
                Error("m5", now.AddMinutes(-2), "APIError", "no provider", null, null, null, null),
                Error("m6", now.AddMinutes(-1), "APIError", "429 Too Many Requests", null, null, "opencode", "deepseek-v4.1-flash-free")
            };
            OpenCodeUsageReport report = Parse(rows, now);
            Check(report.Observations.Count == 6, "basic observations=" + report.Observations.Count);
            Check(report.UsedObserved == 4, "basic used=" + report.UsedObserved);
            Check(report.Tokens == 202, "basic tokens=" + report.Tokens);
            Check(report.Cost == 0.76, "basic cost=" + report.Cost);
            Check(!report.Partial, "basic unexpectedly partial");
            Check(report.HasFreeLimitError, "basic free-limit evidence missing");
            Check(report.LimitExhausted, "basic exhaustion missing");
            Check(report.ExhaustedModel == "deepseek-v4.1-flash-free", "basic exhausted model=" + report.ExhaustedModel);
            Check(!report.LimitNotDetected, "basic limit should not be 'not detected'");
            Check(report.HasUnknownError, "unknown error missing");
            Check(report.Models.Count == 2, "basic model groups=" + report.Models.Count);
        }

        private static void Partial()
        {
            DateTimeOffset now = Utc(2026, 10, 21, 12, 0);
            List<object> rows = new List<object> {
                Step("m1", now.AddMinutes(-2), null, null, null, null, null, 0.2, "deepseek-v4.1-flash-free"),
                Step("m2", now.AddMinutes(-1), 10, 2, 0, 0, 0, null, "deepseek-v4.1-flash-free")
            };
            OpenCodeUsageReport report = Parse(rows, now);
            Check(report.Observations.Count == 2, "partial observations=" + report.Observations.Count);
            Check(report.UsedObserved == 2, "partial used=" + report.UsedObserved);
            Check(report.Tokens == 12, "partial known tokens=" + report.Tokens);
            Check(report.Cost == 0.2, "partial known cost=" + report.Cost);
            Check(report.Partial, "partial flag missing");
        }

        private static void Duplicate()
        {
            DateTimeOffset now = Utc(2026, 10, 21, 12, 0);
            List<object> rows = new List<object> {
                Step("m1", now.AddMinutes(-2), 100, 20, 5, 10, 2, 0.5, "deepseek-v4.1-flash-free"),
                Step("m1", now.AddMinutes(-1), 1, 1, 1, 1, 1, 0.1, "deepseek-v4.1-flash-free")
            };
            OpenCodeUsageReport report = Parse(rows, now);
            Check(report.Observations.Count == 1, "duplicate observations=" + report.Observations.Count);
            Check(report.UsedObserved == 1, "duplicate used=" + report.UsedObserved);
            Check(report.Tokens == 137, "duplicate tokens=" + report.Tokens);
            Check(report.Partial, "duplicate partial flag missing");
        }

        private static void FreeLimitKinds()
        {
            DateTimeOffset now = Utc(2026, 10, 21, 12, 0);
            OpenCodeUsageReport byName = Parse(new List<object> {
                Error("e1", now.AddMinutes(-1), "FreeUsageLimitError", "api error", null, null, "opencode", "deepseek-v4.1-flash-free")
            }, now);
            OpenCodeUsageReport byMessage = Parse(new List<object> {
                Error("e2", now.AddMinutes(-1), "APIError", "Free usage exceeded", null, null, "opencode", "deepseek-v4.1-flash-free")
            }, now);
            OpenCodeUsageReport byBody = Parse(new List<object> {
                Error("e3", now.AddMinutes(-1), "APIError", "api error", "Free limit reached", null, "opencode", "deepseek-v4.1-flash-free")
            }, now);
            OpenCodeUsageReport byMixed = Parse(new List<object> {
                Error("e4", now.AddMinutes(-1), "fReEuSaGeLiMiTeRrOr", "FREE USAGE EXCEEDED", null, null, "opencode", "deepseek-v4.1-flash-free")
            }, now);
            foreach (OpenCodeUsageReport report in new[] { byName, byMessage, byBody, byMixed })
            {
                Check(report.UsedObserved == 0, "free-limit error counted as a request");
                Check(report.HasFreeLimitError, "free-limit evidence missing");
                Check(report.LimitExhausted, "free-limit exhaustion missing");
                Check(report.ExhaustedModel == "deepseek-v4.1-flash-free", "free-limit model missing");
            }
        }

        private static void FreeLimitWithoutModel()
        {
            OpenCodeUsageReport report = Parse(new List<object> {
                Error("e", now.AddMinutes(-1), "FreeUsageLimitError", "free usage exceeded", null, null, null, null)
            }, now);
            Check(report.UsedObserved == 0, "model-less free-limit counted as a request");
            Check(report.HasFreeLimitError, "model-less free-limit evidence missing");
            Check(!report.LimitExhausted, "model-less free-limit must not exhaust a model");
            Check(report.ExhaustedModel == null, "model-less free-limit invented a model");
            Check(report.HasUnknownError, "model-less free-limit must remain unknown");
            Check(!OpenCodeUsageParser.Snapshot(report, now).ZenQuota.Exhausted, "model-less free-limit exhausted quota");
        }

        private static void Aborted()
        {
            OpenCodeUsageReport report = Parse(new List<object> {
                Error("e", now.AddMinutes(-1), "MessageAbortedError", "aborted", null, null, "opencode", "deepseek-v4.1-flash-free")
            }, now);
            Check(report.UsedObserved == 1, "aborted request used=" + report.UsedObserved);
            Check(report.Aborted, "aborted flag missing");
            Check(!report.LimitExhausted, "aborted request marked exhausted");
            Check(OpenCodeUsageParser.Snapshot(report, now).ZenQuota.Reliability == ZenReliability.Low,
                "aborted request reliability is not low");
        }

        private static void GenericErrors()
        {
            DateTimeOffset now = Utc(2026, 10, 21, 12, 0);
            foreach (string text in new[] { "429 Too Many Requests", "403 Forbidden" })
            {
                OpenCodeUsageReport report = Parse(new List<object> {
                    Error("e", now.AddMinutes(-1), "APIError", text, null, null, "opencode", "deepseek-v4.1-flash-free")
                }, now);
                Check(report.UsedObserved == 1, "generic error used=" + report.UsedObserved);
                Check(!report.HasFreeLimitError, "generic error marked as free limit: " + text);
                Check(!report.LimitExhausted, "generic error marked as exhausted: " + text);
                Check(report.LimitNotDetected, "generic error should be 'not detected': " + text);
            }
        }

        private static void RetryAfterCases()
        {
            DateTimeOffset now = Utc(2026, 10, 21, 12, 0);
            Check(RetryReset(now, "retry-after", "120") == now.AddSeconds(120), "retry seconds");
            Check(RetryReset(now, "ReTrY-AfTeR", "120") == now.AddSeconds(120), "retry header case");
            Check(RetryReset(now, "retry-after", "Wed, 21 Oct 2026 12:05:00 GMT") == now.AddMinutes(5), "retry HTTP date");
            Check(RetryReset(now, "retry-after", null) == null, "missing retry header");
            Check(RetryReset(now, "retry-after", "1.5") == null, "fractional retry header");
            Check(RetryReset(now, "retry-after", "Wed, 21 Oct 2026 11:55:00 GMT") == null, "past retry date");
            Check(RetryReset(now, "retry-after", "not a date") == null, "invalid retry header");
        }

        private static DateTimeOffset? RetryReset(DateTimeOffset now, string key, string value)
        {
            Dictionary<string, object> headers = new Dictionary<string, object>();
            if (value != null) headers[key] = value;
            OpenCodeUsageReport report = Parse(new List<object> {
                Error("e", now, "FreeUsageLimitError", "free limit reached", null, headers, "opencode", "deepseek-v4.1-flash-free")
            }, now);
            return report.ObservedResetUtc;
        }

        private static void RetrySnapshots()
        {
            DateTimeOffset at = now;
            Dictionary<string, object> headers = new Dictionary<string, object> { { "retry-after", "120" } };
            OpenCodeUsageReport withRetry = Parse(new List<object> {
                Step("s", at.AddMinutes(-1), 1, 0, 0, 0, 0, 0.1, "deepseek-v4.1-flash-free"),
                Error("e", at, "FreeUsageLimitError", "free limit reached", null, headers, "opencode", "deepseek-v4.1-flash-free")
            }, at);
            UsageSnapshot snapshot = OpenCodeUsageParser.Snapshot(withRetry, at);
            Check(snapshot.ZenQuota.ResetUtc == at.AddSeconds(120), "snapshot retry reset");
            Check(snapshot.ZenQuota.ResetFromRetryAfter, "snapshot retry flag missing");
            Check(snapshot.ZenQuota.Reliability == ZenReliability.High, "snapshot retry reliability");

            OpenCodeUsageReport noRetry = Parse(new List<object> {
                Step("s", at.AddMinutes(-1), 1, 0, 0, 0, 0, 0.1, "deepseek-v4.1-flash-free")
            }, at);
            snapshot = OpenCodeUsageParser.Snapshot(noRetry, at);
            Check(snapshot.ZenQuota.ResetUtc == new DateTimeOffset(at.Year, at.Month, at.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1), "snapshot UTC reset fallback");
            Check(!snapshot.ZenQuota.ResetFromRetryAfter, "snapshot invented retry reset");
            Check(snapshot.ZenQuota.Remaining == 199, "snapshot remaining=" + snapshot.ZenQuota.Remaining);
            Check(snapshot.ZenQuota.Percent == 99.5d, "snapshot percent=" + snapshot.ZenQuota.Percent);
            Check(snapshot.ZenQuota.Status == "healthy", "snapshot status=" + snapshot.ZenQuota.Status);
            Check(snapshot.ZenQuota.Reliability == ZenReliability.Low, "one observation reliability");

            Dictionary<string, object> longer = new Dictionary<string, object> { { "retry-after", "300" } };
            OpenCodeUsageReport latest = Parse(new List<object> {
                Error("e1", at, "FreeUsageLimitError", "free limit reached", null, headers, "opencode", "deepseek-v4.1-flash-free"),
                Error("e2", at, "FreeUsageLimitError", "free limit reached", null, longer, "opencode", "deepseek-v4.1-flash-free")
            }, at);
            Check(OpenCodeUsageParser.Snapshot(latest, at).ZenQuota.ResetUtc == at.AddSeconds(300),
                "snapshot did not keep the latest future reset");
        }

        private static void UtcBoundary()
        {
            long start = OpenCodeUsageParser.StartOfUtcDay(now);
            long next = start + OpenCodeUsageParser.DayMilliseconds;
            Check(OpenCodeUsageParser.StartOfUtcDay(DateTimeOffset.FromUnixTimeMilliseconds(start - 1)) == start - OpenCodeUsageParser.DayMilliseconds,
                "UTC day start before midnight");
            Check(OpenCodeUsageParser.StartOfUtcDay(DateTimeOffset.FromUnixTimeMilliseconds(start)) == start,
                "UTC day start at midnight");
            Check(OpenCodeUsageParser.StartOfUtcDay(DateTimeOffset.FromUnixTimeMilliseconds(next)) == next,
                "UTC day start after midnight");
            string query = OpenCodeUsageParser.Query(start, next);
            Check(query.Contains("p.time_created >= " + start.ToString(CultureInfo.InvariantCulture)), "query start bound");
            Check(query.Contains("p.time_created < " + next.ToString(CultureInfo.InvariantCulture)), "query end bound");
            List<Dictionary<string, object>> today = SqlRows(query);
            Check(today.Count == 3, "UTC boundary today rows=" + today.Count);
            List<string> todayIds = new List<string>();
            foreach (Dictionary<string, object> row in today) todayIds.Add(Text(row, "message_id"));
            Check(todayIds.Contains("boundary-error"), "UTC boundary start row missing");
            Check(todayIds.Contains("today"), "UTC boundary today row missing");
            Check(!todayIds.Contains("boundary-next"), "UTC boundary included the next day");
            List<Dictionary<string, object>> previous = SqlRows(OpenCodeUsageParser.Query(start - OpenCodeUsageParser.DayMilliseconds, start));
            Check(previous.Count == 1, "UTC boundary previous rows=" + previous.Count);
            Check(Text(previous[0], "message_id") == "prev", "UTC boundary previous day missing");
        }

        private static List<Dictionary<string, object>> SqlRows(string sql)
        {
            string sqlite = Environment.GetEnvironmentVariable("T7_SQLITE");
            string database = Environment.GetEnvironmentVariable("T7_DB");
            ProcessStartInfo start = new ProcessStartInfo(sqlite,
                "-json \"" + database.Replace("\"", "\\\"") + "\" \"" + sql.Replace("\"", "\\\"") + "\"");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                Check(process.ExitCode == 0, "sqlite query failed");
                object[] rows = new JavaScriptSerializer().DeserializeObject(output) as object[];
                List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
                foreach (object row in rows) result.Add((Dictionary<string, object>)row);
                return result;
            }
        }

        private static string Text(Dictionary<string, object> row, string key)
        {
            object value;
            return row.TryGetValue(key, out value) ? value as string : null;
        }

        private static void ReaderBasic()
        {
            Environment.SetEnvironmentVariable("T7_DELAY_MS", null);
            using (OpenCodeReader reader = new OpenCodeReader(delegate { return now; }))
            {
                UsageSnapshot snapshot = reader.ReadAsync(CancellationToken.None, 2000).GetAwaiter().GetResult();
                Check(snapshot.OpenCode.UsedObserved == 1, "reader used=" + snapshot.OpenCode.UsedObserved);
                Check(snapshot.TodayTokens == 12, "reader tokens=" + snapshot.TodayTokens);
                Check(snapshot.TodayCost == 0.1, "reader cost=" + snapshot.TodayCost);
                Check(snapshot.OpenCode.HasFreeLimitError, "reader free-limit from database missing");
                Check(snapshot.OpenCode.LimitExhausted, "reader exhaustion from database missing");
                Check(snapshot.OpenCode.ExhaustedModel == "deepseek-v4.1-flash-free", "reader exhausted model=" + snapshot.OpenCode.ExhaustedModel);
                Check(reader.OwnedProcessId == 0, "reader left an owned process");
            }
        }

        private static void ReaderConcurrent()
        {
            Environment.SetEnvironmentVariable("T7_DELAY_MS", "400");
            using (OpenCodeReader reader = new OpenCodeReader(delegate { return now; }))
            {
                Task<UsageSnapshot> first = reader.ReadAsync(CancellationToken.None, 3000);
                Thread.Sleep(100);
                bool rejected = false;
                try { reader.ReadAsync(CancellationToken.None, 3000).GetAwaiter().GetResult(); }
                catch (UsageReadException) { rejected = true; }
                Check(rejected, "concurrent read was not rejected");
                first.GetAwaiter().GetResult();
                Check(reader.OwnedProcessId == 0, "concurrent reader left an owned process");
            }
        }

        private static void ReaderTimeout()
        {
            Environment.SetEnvironmentVariable("T7_DELAY_MS", "5000");
            Environment.SetEnvironmentVariable("T7_CHILD", "1");
            List<int> pids = StartPidLog();
            using (OpenCodeReader reader = new OpenCodeReader(delegate { return now; }))
            {
                Stopwatch watch = Stopwatch.StartNew();
                bool timedOut = false;
                try { reader.ReadAsync(CancellationToken.None, 1000).GetAwaiter().GetResult(); }
                catch (UsageReadException) { timedOut = true; }
                watch.Stop();
                Check(timedOut, "timeout was not reported");
                Check(watch.ElapsedMilliseconds < 8000, "timeout took too long: " + watch.ElapsedMilliseconds);
                Check(reader.OwnedProcessId == 0, "timeout left an owned process");
                CheckPidCount(2);
                Environment.SetEnvironmentVariable("T7_CHILD", null);
                Retry(reader);
            }
            CheckStubPidsGone(pids);
        }

        private static void ReaderCancel()
        {
            Environment.SetEnvironmentVariable("T7_DELAY_MS", "5000");
            Environment.SetEnvironmentVariable("T7_CHILD", "1");
            List<int> pids = StartPidLog();
            using (OpenCodeReader reader = new OpenCodeReader(delegate { return now; }))
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                Task<UsageSnapshot> task = reader.ReadAsync(cancel.Token, 5000);
                WaitForPid(2);
                cancel.Cancel();
                bool cancelled = false;
                try { task.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "cancellation was not reported");
                Check(reader.OwnedProcessId == 0, "cancellation left an owned process");
                Environment.SetEnvironmentVariable("T7_CHILD", null);
                Retry(reader);
            }
            CheckStubPidsGone(pids);
        }

        private static void ReaderOrphan()
        {
            Environment.SetEnvironmentVariable("T7_CHILD", "1");
            Environment.SetEnvironmentVariable("T7_EXIT_EARLY", "1");
            List<int> pids = StartPidLog();
            using (OpenCodeReader reader = new OpenCodeReader(delegate { return now; }))
            {
                bool timedOut = false;
                try { reader.ReadAsync(CancellationToken.None, 1500).GetAwaiter().GetResult(); }
                catch (UsageReadException) { timedOut = true; }
                Check(timedOut, "orphaned child read did not time out");
                Check(reader.OwnedProcessId == 0, "orphaned child left an owned process");
                WaitForPid(2);
                List<int> started = ReadPids();
                Check(started.Count >= 2, "orphan test did not log parent and child");
                Check(!ProcessAlive(started[0]), "orphan parent stayed alive");
                Environment.SetEnvironmentVariable("T7_EXIT_EARLY", null);
                Environment.SetEnvironmentVariable("T7_CHILD", null);
            }
            CheckStubPidsGone(pids);
        }

        private static List<int> ReadPids()
        {
            List<int> result = new List<int>();
            foreach (string line in ReadPidLines())
            {
                int pid;
                if (Int32.TryParse(line.Trim(), out pid)) result.Add(pid);
            }
            return result;
        }
        private static bool ProcessAlive(int pid)
        {
            try { using (Process process = Process.GetProcessById(pid)) return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }
        private static void WaitForPid(int count)
        {
            for (int attempt = 0; attempt < 250; attempt++)
            {
                if (PidCount() >= count) return;
                Thread.Sleep(20);
            }
            throw new Exception("stub process did not start (expected " + count + " PIDs)");
        }

        private static int PidCount()
        {
            int count = 0;
            foreach (string line in ReadPidLines())
            {
                int pid;
                if (Int32.TryParse(line.Trim(), out pid)) count++;
            }
            return count;
        }

        private static string[] ReadPidLines()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using (FileStream stream = new FileStream(pidFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader reader = new StreamReader(stream))
                        return reader.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }
                catch (FileNotFoundException) { return new string[0]; }
                catch (DirectoryNotFoundException) { return new string[0]; }
                catch (IOException) { Thread.Sleep(10); }
            }
            return new string[0];
        }

        private static void CheckPidCount(int minimum)
        {
            int count = PidCount();
            Check(count >= minimum, "expected at least " + minimum + " stub PIDs, got " + count);
        }

        private static void Retry(OpenCodeReader reader)
        {
            Environment.SetEnvironmentVariable("T7_DELAY_MS", null);
            UsageSnapshot retry = reader.ReadAsync(CancellationToken.None, 2000).GetAwaiter().GetResult();
            Check(retry.OpenCode.UsedObserved == 1, "retry after failure used=" + retry.OpenCode.UsedObserved);
            Check(reader.OwnedProcessId == 0, "retry left an owned process");
        }

        private static List<int> StartPidLog()
        {
            if (File.Exists(pidFile)) File.Delete(pidFile);
            return new List<int>();
        }

        private static void CheckStubPidsGone(List<int> pids)
        {
            foreach (string line in ReadPidLines())
            {
                int pid;
                if (!Int32.TryParse(line.Trim(), out pid)) continue;
                pids.Add(pid);
            }
            Check(pids.Count > 0, "no stub process was logged");
            for (int attempt = 0; attempt < 100; attempt++)
            {
                bool alive = false;
                foreach (int pid in pids)
                {
                    try { using (Process process = Process.GetProcessById(pid)) alive = alive || !process.HasExited; }
                    catch (ArgumentException) { }
                }
                if (!alive) return;
                Thread.Sleep(50);
            }
            Check(false, "stub child process is still running");
        }

        private static OpenCodeUsageReport Parse(List<object> rows, DateTimeOffset now)
        {
            return OpenCodeUsageParser.Parse(new JavaScriptSerializer().Serialize(rows), now);
        }


        private static Dictionary<string, object> Step(string id, DateTimeOffset at,
            int? input, int? output, int? reasoning, int? cacheRead, int? cacheWrite, double? cost, string model)
        {
            Dictionary<string, object> message = new Dictionary<string, object> {
                { "providerID", "opencode" }, { "modelID", model }, { "role", "assistant" }
            };
            Dictionary<string, object> part = new Dictionary<string, object> { { "type", "step-finish" } };
            if (input.HasValue)
            {
                part["tokens"] = new Dictionary<string, object> {
                    { "input", input.Value }, { "output", output.Value }, { "reasoning", reasoning.Value },
                    { "cache", new Dictionary<string, object> { { "read", cacheRead.Value }, { "write", cacheWrite.Value } } }
                };
            }
            if (cost.HasValue) part["cost"] = cost.Value;
            return new Dictionary<string, object> {
                { "source", "step" }, { "message_id", id }, { "at", at.ToUnixTimeMilliseconds() },
                { "message_data", new JavaScriptSerializer().Serialize(message) },
                { "part_data", new JavaScriptSerializer().Serialize(part) }
            };
        }

        private static Dictionary<string, object> Error(string id, DateTimeOffset at, string name,
            string message, string body, Dictionary<string, object> headers, string provider, string model)
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            if (message != null) data["message"] = message;
            if (body != null) data["responseBody"] = body;
            if (headers != null) data["responseHeaders"] = headers;
            Dictionary<string, object> error = new Dictionary<string, object> { { "name", name }, { "data", data } };
            Dictionary<string, object> messageObject = new Dictionary<string, object> { { "role", "assistant" }, { "error", error } };
            if (provider != null) messageObject["providerID"] = provider;
            if (model != null) messageObject["modelID"] = model;
            return new Dictionary<string, object> {
                { "source", "error" }, { "message_id", id }, { "at", at.ToUnixTimeMilliseconds() },
                { "message_data", new JavaScriptSerializer().Serialize(messageObject) },
                { "part_data", null }
            };
        }

        private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute)
        {
            return new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
        }

        private static string HashFile(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return Convert.ToBase64String(sha.ComputeHash(stream));
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            passed++;
        }
    }
}

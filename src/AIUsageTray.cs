using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AIUsageTray
{
    internal static class UiText
    {
        internal static readonly bool Russian = UsesRussian(CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        internal static bool UsesRussian(CultureInfo ui, CultureInfo format)
        {
            return RussianCulture(ui) || RussianCulture(format);
        }
        private static bool RussianCulture(CultureInfo culture)
        {
            try { return RussianRegion(new RegionInfo(CultureInfo.CreateSpecificCulture(culture.Name).Name).TwoLetterISORegionName); }
            catch (ArgumentException) { return false; }
        }
        internal static bool RussianRegion(string region)
        {
            return "|AM|AZ|BY|EE|GE|KZ|KG|LV|LT|MD|RU|TJ|TM|UA|UZ|".IndexOf("|" + region + "|", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        internal static string T(string ru, string en) { return Russian ? ru : en; }
    }
    internal sealed class UsageReadException : Exception
    {
        internal UsageReadException(string message) : base(message) { }
    }
    internal sealed class UsageWindow
    {
        internal double? Remaining { get; set; }
        internal DateTimeOffset? ResetUtc { get; set; }
    }
    internal enum CreditsState { Missing, Null, Array }
    internal sealed class ResetCredit
    {
        internal string Title { get; set; }
        internal string Description { get; set; }
        internal string Type { get; set; }
        internal DateTimeOffset? ExpiresUtc { get; set; }
    }
    internal sealed class SubscriptionMetadata
    {
        internal DateTimeOffset? ActiveUntilUtc { get; set; }
        internal DateTimeOffset? LastCheckedUtc { get; set; }
    }
    internal sealed class UsageSnapshot
    {
        internal UsageWindow FiveHour { get; set; }
        internal UsageWindow Weekly { get; set; }
        internal string PlanType { get; set; }
        internal bool ResetCreditsPresent { get; set; }
        internal int? AvailableResetCount { get; set; }
        internal CreditsState CreditsState { get; set; }
        internal List<ResetCredit> Credits { get; set; }
        internal DateTimeOffset ReceivedAtUtc { get; set; }
        internal SubscriptionMetadata Subscription { get; set; }
        internal long? TodayTokens { get; set; }
        internal double? TodayCost { get; set; }
        internal OpenCodeUsageReport OpenCode { get; set; }
        internal ZenQuotaReport ZenQuota { get; set; }
    }
    internal static class UsageParser
    {
        internal const int MaxJsonLength = 4 * 1024 * 1024;
        internal static Dictionary<string, object> Json(string json)
        {
            try
            {
                return new JavaScriptSerializer { MaxJsonLength = MaxJsonLength, RecursionLimit = 64 }
                    .DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (ArgumentException) { throw new UsageReadException(UiText.T("Повреждённый JSON от Codex", "Malformed JSON from Codex")); }
            catch (InvalidOperationException) { throw new UsageReadException(UiText.T("Некорректный ответ Codex", "Invalid response from Codex")); }
        }
        internal static object Field(Dictionary<string, object> data, string key)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) ? value : null;
        }
        internal static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object>; }
        internal static string Text(object value)
        {
            string text = value as string;
            if (String.IsNullOrWhiteSpace(text)) return null;
            return text.Replace("\0", "").Replace("\r", "").Trim();
        }
        internal static double? Number(object value)
        {
            if (!(value is int || value is long || value is double || value is decimal || value is float)) return null;
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return Double.IsNaN(number) || Double.IsInfinity(number) ? (double?)null : number;
        }
        internal static double? GetRemaining(object value)
        {
            double? used = Number(value);
            return used.HasValue ? (double?)Math.Max(0, Math.Min(100, 100 - used.Value)) : null;
        }
        internal static DateTimeOffset? GetResetUtc(object value)
        {
            double? seconds = Number(value);
            if (!seconds.HasValue || seconds.Value != Math.Truncate(seconds.Value) ||
                seconds.Value < -62135596800d || seconds.Value > 253402300799d) return null;
            return new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds.Value);
        }
        internal static DateTimeOffset? OptionalUnixUtc(object value)
        {
            double? number = Number(value);
            if (!number.HasValue) return OptionalDate(value);
            double seconds = Math.Abs(number.Value) >= 100000000000d ? Math.Truncate(number.Value / 1000d) : number.Value;
            return GetResetUtc(seconds);
        }
        internal static DateTimeOffset? OptionalDate(object value)
        {
            string text = value as string;
            if (text == null) return GetResetUtc(value);
            DateTimeOffset date;
            // A timezone is required; do not guess UTC for a date without an offset.
            int t = text.IndexOf('T');
            bool offset = text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ||
                (t >= 0 && (text.IndexOf('+', t) >= 0 || text.IndexOf('-', t) >= 0));
            return offset && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date) ? (DateTimeOffset?)date.ToUniversalTime() : null;
        }
        internal static UsageSnapshot Parse(string json, DateTimeOffset now)
        {
            Dictionary<string, object> envelope = Json(json);
            Dictionary<string, object> result = Map(Field(envelope, "result"));
            if (result == null) throw new UsageReadException(UiText.T("Ответ Codex не содержит результата", "Codex response has no result"));
            Dictionary<string, object> buckets = Map(Field(result, "rateLimitsByLimitId"));
            Dictionary<string, object> bucket = Map(Field(buckets, "codex")) ?? Map(Field(result, "rateLimits"));
            UsageSnapshot snapshot = new UsageSnapshot {
                FiveHour = new UsageWindow(), Weekly = new UsageWindow(), Credits = new List<ResetCredit>(),
                ReceivedAtUtc = now, PlanType = Text(Field(bucket, "planType")) ?? Text(Field(result, "planType"))
            };
            if (bucket != null)
            {
                snapshot.FiveHour = Window(bucket, 300);
                snapshot.Weekly = Window(bucket, 10080);
            }
            snapshot.ResetCreditsPresent = result.ContainsKey("rateLimitResetCredits");
            Dictionary<string, object> resets = Map(Field(result, "rateLimitResetCredits"));
            if (resets != null)
            {
                double? count = Number(Field(resets, "availableCount"));
                if (count.HasValue && count.Value >= 0 && count.Value <= Int32.MaxValue &&
                    count.Value == Math.Truncate(count.Value)) snapshot.AvailableResetCount = (int)count.Value;
                object records;
                if (resets.TryGetValue("credits", out records))
                {
                    snapshot.CreditsState = records == null ? CreditsState.Null : CreditsState.Missing;
                    object[] list = records as object[];
                    if (list != null)
                    {
                        snapshot.CreditsState = CreditsState.Array;
                        foreach (object item in list)
                        {
                            Dictionary<string, object> record = Map(item);
                            if (record == null) continue;
                            snapshot.Credits.Add(new ResetCredit {
                                Title = Text(Field(record, "title")), Type = Text(Field(record, "type")),
                                Description = Text(Field(record, "description")),
                                ExpiresUtc = OptionalDate(Field(record, "expiresAt"))
                            });
                        }
                    }
                }
            }
            return snapshot;
        }
        private static UsageWindow Window(Dictionary<string, object> bucket, int duration)
        {
            UsageWindow found = null;
            foreach (string key in new[] { "primary", "secondary" })
            {
                Dictionary<string, object> window = Map(Field(bucket, key));
                if (Number(Field(window, "windowDurationMins")) != duration) continue;
                UsageWindow current = new UsageWindow {
                    Remaining = GetRemaining(Field(window, "usedPercent")),
                    ResetUtc = GetResetUtc(Field(window, "resetsAt"))
                };
                if (found != null && (found.Remaining != current.Remaining || found.ResetUtc != current.ResetUtc))
                    return new UsageWindow();
                found = current;
            }
            return found ?? new UsageWindow();
        }
        internal static SubscriptionMetadata ParseSubscriptionToken(string token)
        {
            SubscriptionMetadata metadata = new SubscriptionMetadata();
            if (String.IsNullOrEmpty(token) || token.Length > 128 * 1024) return metadata;
            try
            {
                string[] parts = token.Split('.');
                if (parts.Length != 3) return metadata;
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
                Dictionary<string, object> claims = Json(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                Dictionary<string, object> auth = Map(Field(claims, "https://api.openai.com/auth"));
                metadata.ActiveUntilUtc = OptionalDate(Field(auth, "chatgpt_subscription_active_until"));
                metadata.LastCheckedUtc = OptionalDate(Field(auth, "chatgpt_subscription_last_checked"));
            }
            catch (FormatException) { }
            catch (UsageReadException) { }
            return metadata;
        }
        internal static SubscriptionMetadata ReadSavedSubscription()
        {
            try
            {
                string home = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (String.IsNullOrEmpty(home)) home = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                string path = Path.Combine(home, "auth.json");
                FileInfo file = new FileInfo(path);
                if (!file.Exists || file.Length > 2 * 1024 * 1024) return null;
                Dictionary<string, object> auth = Json(File.ReadAllText(path, Encoding.UTF8));
                string token = Field(Map(Field(auth, "tokens")), "id_token") as string;
                SubscriptionMetadata data = ParseSubscriptionToken(token);
                return data.ActiveUntilUtc.HasValue || data.LastCheckedUtc.HasValue ? data : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (UsageReadException) { return null; }
            catch (ArgumentException) { return null; }
        }
    }
    internal static class CodexLocator
    {
        private static string cached;
        internal static string Find(CancellationToken cancel)
        {
            if (!String.IsNullOrEmpty(cached) && File.Exists(cached)) return cached;
            foreach (string raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                cancel.ThrowIfCancellationRequested();
                string directory = raw.Trim().Trim('"');
                if (!Absolute(directory)) continue;
                try
                {
                    string path = Path.Combine(directory, "codex.exe");
                    if (File.Exists(path)) return cached = Path.GetFullPath(path);
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            ProcessStartInfo start = Hidden(powershell,
                "-NoLogo -NoProfile -NonInteractive -Command \"$p=Get-AppxPackage -Name OpenAI.Codex; foreach($i in $p){[Console]::WriteLine($i.InstallLocation)}\"");
            using (Process process = new Process { StartInfo = start })
            {
                try
                {
                    process.Start();
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> errors = process.StandardError.ReadToEndAsync();
                    using (cancel.Register(delegate { Stop(process); }))
                    {
                        Stopwatch deadline = Stopwatch.StartNew();
                        while (!process.WaitForExit(50))
                        {
                            cancel.ThrowIfCancellationRequested();
                            if (deadline.ElapsedMilliseconds >= 5000)
                                throw new UsageReadException(UiText.T("Поиск установленного Codex превысил 5 секунд", "Locating installed Codex exceeded 5 seconds"));
                        }
                        cancel.ThrowIfCancellationRequested();
                        if (output.Wait(500) && output.Result.Length <= 32768)
                        {
                            foreach (string line in output.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                if (!Absolute(line.Trim())) continue;
                                string path = Path.Combine(line.Trim(), @"app\resources\codex.exe");
                                if (File.Exists(path)) return cached = Path.GetFullPath(path);
                            }
                        }
                        errors.Wait(500); // Drain only; errors may contain installation details.
                    }
                }
                catch (System.ComponentModel.Win32Exception) { }
                finally { Stop(process); }
            }
            throw new UsageReadException(UiText.T("Codex не найден. Установите приложение Codex и войдите в аккаунт", "Codex not found. Install the Codex app and sign in"));
        }
        internal static bool Absolute(string path)
        {
            return path.Length >= 3 && ((Char.IsLetter(path[0]) && path[1] == ':' &&
                (path[2] == '\\' || path[2] == '/')) || path.StartsWith(@"\\", StringComparison.Ordinal));
        }
        internal static ProcessStartInfo Hidden(string file, string arguments)
        {
            return new ProcessStartInfo {
                FileName = file, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
        }
        internal static void Stop(Process process)
        {
            try { if (!process.HasExited) { process.Kill(); process.WaitForExit(1000); } }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        internal static void ClearCache() { cached = null; }
    }
    internal sealed class ReadMetrics
    {
        internal int ProcessId { get; set; }
        internal double ResponseMilliseconds { get; set; }
        internal double CpuMilliseconds { get; set; }
        internal long PeakWorkingSet { get; set; }
        internal long PrivateAtResponse { get; set; }
        internal double CleanupMilliseconds { get; set; }
        internal bool Exited { get; set; }
    }
    internal sealed class CodexReader : IDisposable
    {
        private readonly object processLock = new object();
        private Process owned;
        private bool disposed;
        private bool cleanupFailed;
        private int reading;
        internal ReadMetrics LastMetrics { get; private set; }
        internal int OwnedProcessId
        {
            get { lock (processLock) { return owned == null ? 0 : owned.Id; } }
        }
        internal Task<UsageSnapshot> ReadAsync(CancellationToken stop)
        {
            return ReadAsync(null, stop, 15000);
        }
        internal async Task<UsageSnapshot> ReadAsync(ProcessStartInfo testLaunch, CancellationToken stop, int timeoutMilliseconds)
        {
            if (Interlocked.CompareExchange(ref reading, 1, 0) != 0)
                throw new UsageReadException(UiText.T("Чтение Codex уже выполняется", "A Codex read is already in progress"));
            try
            {
                using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    deadline.CancelAfter(timeoutMilliseconds);
                    return await Task.Run(delegate { return Read(testLaunch, deadline.Token, stop); }).ConfigureAwait(false);
                }
            }
            finally { Interlocked.Exchange(ref reading, 0); }
        }
        // Adapted initialize/correlation/bucket selection from Tooblippe (embedded MIT notice).
        private UsageSnapshot Read(ProcessStartInfo launch, CancellationToken deadline, CancellationToken stop)
        {
            bool readSavedMetadata = launch == null;
            Process process = null;
            Task errors = null;
            CancellationTokenRegistration cancel = new CancellationTokenRegistration();
            ReadMetrics metrics = new ReadMetrics();
            LastMetrics = metrics;
            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                if (launch == null) launch = CodexLocator.Hidden(CodexLocator.Find(deadline), "app-server --listen stdio://");
                deadline.ThrowIfCancellationRequested();
                process = new Process { StartInfo = launch };
                lock (processLock)
                {
                    if (disposed) throw new OperationCanceledException(stop);
                    if (cleanupFailed) throw new UsageReadException(UiText.T("Предыдущий процесс Codex не завершён", "The previous Codex process has not exited"));
                    process.Start();
                    owned = process;
                    metrics.ProcessId = process.Id;
                }
                cancel = deadline.Register(KillOwned);
                deadline.ThrowIfCancellationRequested();
                errors = Task.Run(delegate {
                    try { char[] buffer = new char[4096]; while (process.StandardError.Read(buffer, 0, buffer.Length) != 0) { } }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                });
                Send(process, "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"ai-usage-tray\",\"version\":\"2.0.0\"},\"capabilities\":{\"experimentalApi\":true}}}");
                Response(process, 1, deadline);
                Send(process, "{\"method\":\"initialized\",\"params\":null}");
                Send(process, "{\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":null}");
                UsageSnapshot snapshot = UsageParser.Parse(Response(process, 2, deadline), DateTimeOffset.UtcNow);
                metrics.ResponseMilliseconds = elapsed.Elapsed.TotalMilliseconds;
                if (String.IsNullOrEmpty(snapshot.PlanType))
                {
                    try
                    {
                        Send(process, "{\"id\":3,\"method\":\"account/read\",\"params\":null}");
                        Dictionary<string, object> account = UsageParser.Map(UsageParser.Field(UsageParser.Json(Response(process, 3, deadline)), "result"));
                        snapshot.PlanType = UsageParser.Text(UsageParser.Field(UsageParser.Map(UsageParser.Field(account, "account")), "planType")) ??
                            UsageParser.Text(UsageParser.Field(account, "planType"));
                    }
                    catch (UsageReadException) { }
                    catch (IOException) { }
                    catch (OperationCanceledException) { if (stop.IsCancellationRequested) throw; }
                }
                stop.ThrowIfCancellationRequested();
                Capture(process, metrics);
                snapshot.Subscription = readSavedMetadata ? UsageParser.ReadSavedSubscription() : null;
                return snapshot;
            }
            catch (OperationCanceledException)
            {
                if (stop.IsCancellationRequested) throw;
                throw new UsageReadException(UiText.T("Codex не ответил за 15 секунд", "Codex did not respond within 15 seconds"));
            }
            catch (System.ComponentModel.Win32Exception)
            {
                CodexLocator.ClearCache();
                throw new UsageReadException(UiText.T("Не удалось запустить установленный Codex", "Unable to start installed Codex"));
            }
            catch (IOException)
            {
                if (stop.IsCancellationRequested) throw new OperationCanceledException(stop);
                throw new UsageReadException(deadline.IsCancellationRequested ?
                    UiText.T("Codex не ответил за 15 секунд", "Codex did not respond within 15 seconds") :
                    UiText.T("Codex закрыл соединение", "Codex closed the connection"));
            }
            finally
            {
                cancel.Dispose();
                if (process != null)
                {
                    Capture(process, metrics);
                    Stopwatch cleanup = Stopwatch.StartNew();
                    try
                    {
                        try { process.StandardInput.Close(); } catch (IOException) { } catch (InvalidOperationException) { }
                        if (!process.WaitForExit(500)) CodexLocator.Stop(process);
                        metrics.Exited = process.HasExited;
                        if (errors != null) errors.Wait(500);
                    }
                    catch (InvalidOperationException) { metrics.Exited = metrics.ProcessId == 0; }
                    finally
                    {
                        metrics.CleanupMilliseconds = cleanup.Elapsed.TotalMilliseconds;
                        lock (processLock)
                        {
                            if (owned == process) owned = null;
                            cleanupFailed = metrics.ProcessId != 0 && !metrics.Exited;
                        }
                        process.Dispose();
                    }
                    if (cleanupFailed) throw new UsageReadException(UiText.T("Не удалось завершить собственный процесс Codex", "Unable to stop the owned Codex process"));
                }
            }
        }
        private static void Capture(Process process, ReadMetrics metrics)
        {
            try
            {
                process.Refresh();
                metrics.CpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds;
                metrics.PeakWorkingSet = Math.Max(metrics.PeakWorkingSet, process.PeakWorkingSet64);
                if (!process.HasExited) metrics.PrivateAtResponse = process.PrivateMemorySize64;
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        private static void Send(Process process, string line)
        {
            process.StandardInput.WriteLine(line);
            process.StandardInput.Flush();
        }
        private static string Response(Process process, int id, CancellationToken cancel)
        {
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                StringBuilder line = new StringBuilder();
                int character;
                while ((character = process.StandardOutput.Read()) != -1 && character != '\n')
                {
                    if (line.Length >= UsageParser.MaxJsonLength) throw new UsageReadException(UiText.T("Ответ Codex слишком большой", "Codex response is too large"));
                    line.Append((char)character);
                }
                cancel.ThrowIfCancellationRequested();
                if (character == -1 && line.Length == 0) throw new UsageReadException(UiText.T("Codex завершился без ответа", "Codex exited without a response"));
                if (line.Length == 0) continue;
                Dictionary<string, object> envelope = UsageParser.Json(line.ToString());
                object responseId = UsageParser.Field(envelope, "id");
                if (envelope == null) throw new UsageReadException(UiText.T("Некорректный ответ Codex", "Invalid response from Codex"));
                if (envelope.ContainsKey("method") || !(responseId is int || responseId is long) ||
                    Convert.ToInt64(responseId, CultureInfo.InvariantCulture) != id) continue;
                Dictionary<string, object> error = UsageParser.Map(UsageParser.Field(envelope, "error"));
                if (error != null)
                {
                    // Categorize in memory; never expose the server's raw error or stderr.
                    string message = (UsageParser.Field(error, "message") as string ?? "").ToLowerInvariant();
                    object code = UsageParser.Field(error, "code");
                    if (Object.Equals(code, 403) || message.Contains("403") || message.Contains("forbidden"))
                        throw new UsageReadException(UiText.T("Сервис Codex отказал в доступе (403)", "Codex service denied access (403)"));
                    if (Object.Equals(code, 401) || message.Contains("401") || message.Contains("unauthorized") ||
                        message.Contains("auth") || message.Contains("sign in") || message.Contains("log in"))
                        throw new UsageReadException(UiText.T("Вход в Codex требует проверки", "Codex sign-in needs attention"));
                    throw new UsageReadException(UiText.T("Ошибка сервиса Codex", "Codex service error"));
                }
                if (!envelope.ContainsKey("result")) throw new UsageReadException(UiText.T("Ответ Codex не содержит результата", "Codex response has no result"));
                return line.ToString();
            }
        }
        private void KillOwned()
        {
            lock (processLock) { if (owned != null) CodexLocator.Stop(owned); }
        }
        public void Dispose()
        {
            lock (processLock) { disposed = true; }
            KillOwned();
        }
    }
    internal static class TrayIconRenderer
    {
        internal static Color ColorFor(double? value, bool trayIcon = false)
        {
            return !value.HasValue ? Color.FromArgb(148, 163, 184) :
                value.Value > 30 ? (trayIcon ? Color.FromArgb(0, 176, 0) : Color.FromArgb(34, 197, 94)) :
                value.Value > 10 ? Color.FromArgb(234, 179, 8) : Color.FromArgb(239, 68, 68);
        }
        internal static string FormatPercent(double? value)
        {
            return value.HasValue ? Math.Floor(value.Value).ToString(CultureInfo.InvariantCulture) + "%" : "—";
        }
        internal static string FormatTokensCompact(long? tokens)
        {
            if (!tokens.HasValue) return "—";
            double value = tokens.Value;
            if (value < 1000d) return tokens.Value.ToString(CultureInfo.InvariantCulture);
            if (value < 1000000d) return (value / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + "K";
            if (value < 1000000000d) return (value / 1000000d).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            return (value / 1000000000d).ToString("0.0", CultureInfo.InvariantCulture) + "B";
        }
        internal static string FormatTokensFull(long? tokens)
        {
            return tokens.HasValue ? tokens.Value.ToString("N0", CultureInfo.CurrentCulture) : "—";
        }
        internal static string FormatCost(double? cost)
        {
            return cost.HasValue ? "$" + cost.Value.ToString("0.00", CultureInfo.InvariantCulture) : "—";
        }
        // Adapted glyph fitting / HICON ownership from VictorZakharov (embedded MIT notice).
        internal static Icon Create(double? five, double? weekly, int pixels)
        {
            return Create(FormatPercent(five), FormatPercent(weekly), ColorFor(five, true), ColorFor(weekly, true), pixels);
        }
        internal static Icon Create(string top, string bottom, Color topColor, Color bottomColor, int pixels)
        {
            const int scale = 8;
            using (Bitmap large = new Bitmap(pixels * scale, pixels * scale, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(large))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float gap = large.Height / 8f;
                float rowHeight = (large.Height - gap) / 2;
                Draw(g, top, topColor, new RectangleF(0, 0, large.Width, rowHeight));
                Draw(g, bottom, bottomColor, new RectangleF(0, rowHeight + gap, large.Width, rowHeight));
                using (Bitmap small = new Bitmap(pixels, pixels, PixelFormat.Format32bppArgb))
                using (Graphics output = Graphics.FromImage(small))
                {
                    output.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    output.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    output.DrawImage(large, new Rectangle(0, 0, pixels, pixels));
                    // Keep the background transparent and every text pixel fully opaque.
                    for (int y = 0; y < pixels; y++)
                    for (int x = 0; x < pixels; x++)
                        small.SetPixel(x, y, small.GetPixel(x, y).A < 64 ? Color.Transparent :
                            (y < pixels / 2 ? topColor : bottomColor));
                    IntPtr handle = small.GetHicon();
                    try { using (Icon borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
                    finally { Native.DestroyIcon(handle); }
                }
            }
        }
        private static void Draw(Graphics graphics, string text, Color color, RectangleF slot)
        {
            using (GraphicsPath path = new GraphicsPath())
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
            {
                path.AddString(text, FontFamily.GenericSansSerif, (int)FontStyle.Bold, 100, PointF.Empty, format);
                RectangleF glyph = path.GetBounds();
                float widthFit = slot.Width / glyph.Width;
                float heightFit = Math.Min(slot.Height / glyph.Height, widthFit);
                using (Matrix matrix = new Matrix(widthFit, 0, 0, heightFit,
                    slot.Left - glyph.Left * widthFit,
                    slot.Top + (slot.Height - glyph.Height * heightFit) / 2 - glyph.Top * heightFit))
                using (SolidBrush brush = new SolidBrush(color))
                {
                    path.Transform(matrix);
                    graphics.FillPath(brush, path);
                }
            }
        }
    }
    internal sealed class PlanLabel : Label
    {
        internal string Plan { get; set; }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (String.IsNullOrEmpty(Plan)) { base.OnPaint(e); return; }
            string prefix = Text.Substring(0, Text.Length - Plan.Length);
            using (Font planFont = new Font(Font, Font.Style | FontStyle.Bold))
            {
                int prefixWidth = TextRenderer.MeasureText(e.Graphics, prefix, Font, Size, TextFormatFlags.NoPrefix).Width;
                int planWidth = TextRenderer.MeasureText(e.Graphics, Plan, planFont, Size, TextFormatFlags.NoPrefix).Width;
                int x = Math.Max(0, (ClientSize.Width - prefixWidth - planWidth) / 2);
                TextRenderer.DrawText(e.Graphics, prefix, Font, new Point(x, 0), ForeColor, TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(e.Graphics, Plan, planFont, new Point(x + prefixWidth, 0), ForeColor, TextFormatFlags.NoPrefix);
            }
        }
    }
    internal sealed class PopupForm : Form
    {
        private const int ScreenGap = 6;
        private readonly Panel body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        private readonly System.Windows.Forms.Timer countdown = new System.Windows.Forms.Timer { Interval = 1000 };
        private Font normal;
        private Font bold;
        private int dpi;
        private int padding;
        private Label fiveCountdown;
        private Label weekCountdown;
        private Label quotaCountdown;
        private Label status;
        private string provider;
        private UsageSnapshot snapshot;
        private string error;
        private ProviderStatus providerStatus;
        private Rectangle anchor;
        private Rectangle layoutWork;
        private bool needsBuild = true;
        internal int CreditRows { get; private set; }
        internal bool ScrollNeeded { get { return body.AutoScrollMinSize.Height > body.ClientSize.Height; } }
        internal PopupForm(string providerName)
        {
            provider = providerName;
            Text = "AI Usage Tray";
            UpdateAccessibleName();
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            ControlBox = false;
            MaximizeBox = MinimizeBox = false;
            TopMost = true;
            BackColor = body.BackColor = SystemColors.Window;
            ForeColor = body.ForeColor = SystemColors.WindowText;
            Controls.Add(body);
            countdown.Tick += delegate { UpdateTime(DateTimeOffset.UtcNow); };
            VisibleChanged += delegate { countdown.Enabled = Visible; };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams value = base.CreateParams; value.Style |= 0x00800000; value.ExStyle |= 0x02000080; return value; }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.ApplyPopupFrame(Handle);
        }
        internal void SetSnapshot(UsageSnapshot value, string failure, ProviderStatus state)
        {
            snapshot = value;
            error = failure;
            providerStatus = state;
            needsBuild = true;
            if (Visible) ShowCard(anchor, false);
        }
        internal void SetProvider(string providerName)
        {
            provider = providerName;
            UpdateAccessibleName();
            needsBuild = true;
            if (Visible) ShowCard(anchor, false);
        }
        private void UpdateAccessibleName()
        {
            AccessibleName = provider == null ? UiText.T("Нет провайдера", "No provider") :
                provider + UiText.T(" · данные провайдера", " · provider data");
        }
        internal void ShowCard(Rectangle anchor, bool activate)
        {
            this.anchor = anchor;
            Rectangle work = Screen.FromPoint(new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2)).WorkingArea;
            int newDpi = Native.DpiAt(new Point(anchor.Left, anchor.Top));
            if (needsBuild || dpi != newDpi || layoutWork != work) Build(work, newDpi);
            else { UpdateTime(DateTimeOffset.UtcNow, false); FitToText(work); }
            Bounds = Place(anchor, Size, work);
            if (!Visible) Show();
            if (activate) { Activate(); Focus(); }
        }
        internal static Rectangle Place(Rectangle anchor, Size size, Rectangle work)
        {
            int width = Math.Min(size.Width, Math.Max(1, work.Width - ScreenGap));
            int height = Math.Min(size.Height, work.Height);
            int x = Math.Max(work.Left, work.Right - width - ScreenGap);
            int y = anchor.Bottom + ScreenGap;
            if (y + height > work.Bottom) y = anchor.Top - height - ScreenGap;
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
            return new Rectangle(x, y, width, height);
        }
        private void Build(Rectangle work, int newDpi)
        {
            if (!IsHandleCreated) CreateHandle();
            SuspendLayout();
            body.SuspendLayout();
            try
            {
                body.AutoScrollPosition = Point.Empty;
                while (body.Controls.Count != 0) body.Controls[0].Dispose();
                fiveCountdown = null;
                weekCountdown = null;
                quotaCountdown = null;
                if (normal != null) normal.Dispose();
                if (bold != null) bold.Dispose();
                dpi = newDpi;
                Font system = SystemFonts.MessageBoxFont;
                normal = new Font(system.FontFamily, system.SizeInPoints * dpi / 72f, system.Style, GraphicsUnit.Pixel);
                bold = new Font(normal, normal.Style | FontStyle.Bold);
                bool dark = Native.PopupDark;
                BackColor = body.BackColor = dark ? Color.FromArgb(32, 32, 32) : SystemColors.Window;
                ForeColor = body.ForeColor = dark ? Color.White : SystemColors.WindowText;
                if (IsHandleCreated) Native.ApplyPopupFrame(Handle);
                padding = 12 * dpi / 96;
                string plan = snapshot == null ? null : snapshot.PlanType;
                if (!String.IsNullOrEmpty(plan)) plan = plan.ToUpperInvariant();
                string title = provider == null ? UiText.T("Нет провайдера", "No provider") :
                    String.IsNullOrEmpty(plan) ? provider : provider + " · " + plan;
                AddTitle(title, plan, 10);
                if (provider == "OpenCode")
                {
                    BuildOpenCode(DateTimeOffset.UtcNow);
                }
                else if (provider != null)
                {
                    Window(snapshot == null ? new UsageWindow() : snapshot.FiveHour, true);
                    Window(snapshot == null ? new UsageWindow() : snapshot.Weekly, false);
                    string count = snapshot != null && snapshot.AvailableResetCount.HasValue ?
                        snapshot.AvailableResetCount.Value.ToString(CultureInfo.CurrentCulture) : UiText.T("нет данных", "not available");
                    Label resets = Add(UiText.T("Сбросов лимитов: ", "Limit resets: ") + count, 10);
                    CreditRows = 0;
                    if (snapshot != null)
                    {
                        foreach (ResetCredit credit in snapshot.Credits)
                        {
                            if (snapshot.AvailableResetCount.HasValue && CreditRows >= snapshot.AvailableResetCount.Value) break;
                            CreditRows++;
                            Add(UiText.T("До: ", "Until: ") + (credit.ExpiresUtc.HasValue ? LocalDate(credit.ExpiresUtc.Value) : UiText.T("нет данных", "not available")), 2);
                        }
                    }
                    if (CreditRows > 0)
                    {
                        resets.Tag = 2;
                        body.Controls[body.Controls.Count - 1].Tag = 10;
                    }
                }
                SubscriptionMetadata metadata = snapshot == null ? null : snapshot.Subscription;
                if (metadata != null && metadata.ActiveUntilUtc.HasValue)
                    Add(UiText.T("Подписка до: ", "Subscription until: ") + LocalDate(metadata.ActiveUntilUtc.Value), 0);
                status = Add("", 0);
                UpdateTime(DateTimeOffset.UtcNow, false);
                FitToText(work);
                layoutWork = work;
                needsBuild = false;
            }
            finally { body.ResumeLayout(); ResumeLayout(); }
        }
        private void BuildOpenCode(DateTimeOffset now)
        {
            ZenQuotaReport quota = snapshot == null ? null : snapshot.ZenQuota;
            bool hasData = quota != null && quota.HasData;
            string connection;
            if (!hasData) connection = UiText.T("нет данных", "not available");
            else
            {
                bool cli = true;
                try { OpenCodeLocator.Find(CancellationToken.None); }
                catch (UsageReadException) { cli = false; }
                connection = cli ?
                    UiText.T("локальная база (opencode db)", "local database (opencode db)") :
                    UiText.T("локальная база; CLI не найден", "local database; CLI not found");
            }
            Add(UiText.T("Подключение: ", "Connection: ") + connection, 10);
            Add(UiText.T("Бесплатная квота Zen (оценка, UTC)", "Zen free quota (estimate, UTC)"), 10, true);

            string noData = UiText.T("нет данных", "not available");
            string used = hasData ?
                quota.UsedObserved.ToString(CultureInfo.CurrentCulture) + UiText.T(" из ~", " of ~") +
                quota.BaseLimit.ToString(CultureInfo.CurrentCulture) + UiText.T(" запросов", " requests") : noData;
            Add(UiText.T("Использовано: ", "Used: ") + used, 2);

            string remaining = noData;
            if (hasData && quota.Remaining.HasValue && quota.Percent.HasValue)
                remaining = "~" + quota.Remaining.Value.ToString(CultureInfo.CurrentCulture) +
                    " (" + quota.Percent.Value.ToString("0.#", CultureInfo.CurrentCulture) + "%)";
            Add(UiText.T("Остаток: ", "Remaining: ") + remaining, 2);

            if (hasData && quota.Remaining.HasValue && quota.RemainingUpperBound.HasValue &&
                quota.RemainingUpperBound.Value > quota.Remaining.Value)
                Add(UiText.T("Диапазон: ", "Range: ") + quota.Remaining.Value.ToString(CultureInfo.CurrentCulture) +
                    "–" + quota.RemainingUpperBound.Value.ToString(CultureInfo.CurrentCulture), 2);

            if (hasData && quota.ExhaustionForecastUtc.HasValue)
                Add(UiText.T("Исчерпание ожидается: ", "Exhaustion expected: ") + LocalDate(quota.ExhaustionForecastUtc.Value), 2);

            Label timer = Add(UiText.T("До сброса: ", "Time to reset: ") +
                Countdown(hasData ? quota.ResetUtc : null, now), 2);
            quotaCountdown = timer;

            string reset = noData;
            if (hasData && quota.ResetUtc.HasValue)
                reset = LocalDate(quota.ResetUtc.Value) +
                    (quota.ResetFromRetryAfter ? " (Retry-After)" : " (UTC)");
            Add(UiText.T("Сброс: ", "Reset: ") + reset, 10);

            Add(UiText.T("Достоверность: ", "Confidence: ") + ReliabilityText(quota, hasData), 2);
            Add(ExhaustionText(quota, hasData), 10);

            if (quota != null && quota.HasUnknownError)
            {
                string model = String.IsNullOrEmpty(quota.UnknownErrorModel) ? "" : " (" + quota.UnknownErrorModel + ")";
                Add(UiText.T("Неизвестная ошибка", "Unknown error") + model, 10);
            }

            if (quota != null && quota.Models != null && quota.Models.Count > 0)
            {
                Add(UiText.T("Модели:", "Models:"), 6, true);
                foreach (ZenModelQuota model in quota.Models) Add(ModelLine(model), 2);
            }

            string tokens = snapshot != null && snapshot.TodayTokens.HasValue ?
                TrayIconRenderer.FormatTokensFull(snapshot.TodayTokens) : noData;
            string cost = snapshot != null && snapshot.TodayCost.HasValue ?
                TrayIconRenderer.FormatCost(snapshot.TodayCost) : noData;
            Add(UiText.T("Токены за сегодня (UTC): ", "Tokens today (UTC): ") + tokens, 10);
            Add(UiText.T("Стоимость за сегодня (оценка, UTC): ", "Cost today (estimated, UTC): ") + cost, 2);
        }

        private static string ReliabilityText(ZenQuotaReport quota, bool hasData)
        {
            if (!hasData || quota == null) return UiText.T("нет данных", "not available");
            if (quota.Reliability == ZenReliability.High) return UiText.T("высокая", "high");
            if (quota.Reliability == ZenReliability.Medium) return UiText.T("средняя", "medium");
            if (quota.Reliability == ZenReliability.Low) return UiText.T("низкая", "low");
            return UiText.T("нет данных", "not available");
        }

        private static string ExhaustionText(ZenQuotaReport quota, bool hasData)
        {
            if (!hasData || quota == null) return UiText.T("Исчерпание: нет данных", "Exhaustion: not available");
            if (quota.Exhausted)
            {
                string model = String.IsNullOrEmpty(quota.ExhaustedModel) ? "" : " (" + quota.ExhaustedModel + ")";
                return UiText.T("Исчерпание: обнаружено", "Exhaustion: detected") + model;
            }
            if (quota.LimitNotDetected) return UiText.T("Исчерпание: не обнаружено", "Exhaustion: not detected");
            return UiText.T("Исчерпание: нет данных", "Exhaustion: not available");
        }

        private static string ModelLine(ZenModelQuota model)
        {
            string text = UiText.T("Модель: ", "Model: ") + (model.Model ?? "—") + " — " +
                model.Observations.ToString(CultureInfo.CurrentCulture) +
                UiText.T(" наблюдений", " observations");
            if (model.Tokens.HasValue)
                text += UiText.T("; токены ", "; tokens ") + TrayIconRenderer.FormatTokensFull(model.Tokens);
            if (model.LimitKnown && model.DailyLimit.HasValue)
                text += UiText.T("; лимит: ", "; limit: ") + model.DailyLimit.Value.ToString(CultureInfo.CurrentCulture);
            text += model.Exhausted ? UiText.T("; исчерпание: да", "; exhaustion: yes") :
                UiText.T("; исчерпание: нет", "; exhaustion: no");
            return text;
        }

        private void Window(UsageWindow window, bool five)
        {
            string percent = window.Remaining.HasValue ?
                window.Remaining.Value.ToString("0.#", CultureInfo.CurrentCulture) + "%" : UiText.T("нет данных", "not available");
            Add((five ? UiText.T("5 часов", "5 hours") : UiText.T("Неделя", "Week")) + " - " + percent, 2, true,
                TrayIconRenderer.ColorFor(window.Remaining, true));
            Label timer = Add(UiText.T("До сброса: ", "Time to reset: ") + Countdown(window.ResetUtc, DateTimeOffset.UtcNow),
                2);
            if (five) fiveCountdown = timer; else weekCountdown = timer;
            Add(UiText.T("Сброс: ", "Reset: ") + (window.ResetUtc.HasValue ? LocalDate(window.ResetUtc.Value) : UiText.T("нет данных", "not available")),
                10);
        }
        private Label AddTitle(string text, string plan, int gap)
        {
            PlanLabel label = new PlanLabel { Text = text, Plan = plan, Font = normal, ForeColor = body.ForeColor,
                BackColor = body.BackColor, UseMnemonic = false, AutoSize = false,
                AccessibleName = text, Tag = gap, TextAlign = ContentAlignment.TopCenter };
            body.Controls.Add(label);
            return label;
        }
        private Label Add(string text, int gap, bool strong = false, Color? color = null)
        {
            Label label = new Label { Text = text, Font = strong ? bold : normal, ForeColor = color.HasValue ? color.Value : body.ForeColor,
                BackColor = body.BackColor, UseMnemonic = false, AutoSize = false,
                AccessibleName = text, Tag = gap, TextAlign = ContentAlignment.TopCenter
            };
            body.Controls.Add(label);
            return label;
        }
        private void FitToText(Rectangle work)
        {
            Point scrollPosition = body.AutoScrollPosition;
            body.SuspendLayout();
            try
            {
                body.AutoScrollPosition = Point.Empty;
                int longest = 1;
                foreach (Control label in body.Controls)
                    if (label.Text.Length > 0)
                        longest = Math.Max(longest, TextRenderer.MeasureText(label.Text,
                            label.Font, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPrefix).Width);
                int scrollbar = 0;
                int frameWidth = Width - ClientSize.Width;
                int maxHeight = Math.Max(1, work.Height - 8 - (Height - ClientSize.Height));
                // Wider OpenCode rows need a small GDI measurement margin.
                int safety = String.Equals(provider, "OpenCode", StringComparison.Ordinal) ? 24 : 0;
                for (int pass = 0; pass < 2; pass++)
                {
                    int width = Math.Min(Math.Max(1, work.Width - ScreenGap - frameWidth), longest + padding * 2 + scrollbar + safety);
                    int rowWidth = Math.Max(1, width - padding * 2 - scrollbar);
                    int rowY = padding;
                    foreach (Control label in body.Controls)
                    {
                        label.Visible = label.Text.Length > 0;
                        if (label.Text.Length == 0) { label.Bounds = Rectangle.Empty; continue; }
                        Size measured = TextRenderer.MeasureText(label.Text, label.Font,
                            new Size(rowWidth, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
                        int labelHeight = Math.Max(label.Font.Height, measured.Height);
                        if (safety > 0)
                        {
                            // GDI can wrap a wide OpenCode row while MeasureText reports one line.
                            Size full = TextRenderer.MeasureText(label.Text, label.Font,
                                new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPrefix);
                            int lines = Math.Max(1, (int)Math.Ceiling((full.Width + safety) / (double)rowWidth));
                            labelHeight = Math.Max(labelHeight, lines * label.Font.Height);
                        }
                        label.Bounds = new Rectangle(padding, rowY, rowWidth, labelHeight + 2);
                        rowY = label.Bottom + (int)label.Tag * dpi / 96;
                    }
                    rowY += padding;
                    body.AutoScrollMinSize = new Size(0, rowY);
                    ClientSize = new Size(width, Math.Min(rowY, maxHeight));
                    if (pass == 0 && rowY > maxHeight)
                    { scrollbar = SystemInformation.VerticalScrollBarWidth * dpi / 96; continue; }
                    break;
                }
                Bounds = Place(anchor, Size, work);
                body.AutoScrollPosition = new Point(-scrollPosition.X, -scrollPosition.Y);
            }
            finally { body.ResumeLayout(); }
        }
        internal void UpdateTime(DateTimeOffset now, bool resize = true)
        {
            bool changed = false;
            if (snapshot != null)
            {
                changed |= SetText(fiveCountdown, UiText.T("До сброса: ", "Time to reset: ") + Countdown(snapshot.FiveHour.ResetUtc, now));
                changed |= SetText(weekCountdown, UiText.T("До сброса: ", "Time to reset: ") + Countdown(snapshot.Weekly.ResetUtc, now));
                if (quotaCountdown != null)
                {
                    ZenQuotaReport quota = snapshot.ZenQuota;
                    DateTimeOffset? reset = quota != null && quota.HasData ? quota.ResetUtc : null;
                    changed |= SetText(quotaCountdown, UiText.T("До сброса: ", "Time to reset: ") + Countdown(reset, now));
                }
            }
            changed |= SetText(status, StatusText(provider, providerStatus, snapshot, error, now));
            if (changed && resize && Visible) FitToText(Screen.FromControl(this).WorkingArea);
        }
        private static bool SetText(Label label, string text)
        {
            if (label == null || label.Text == text) return false;
            label.Text = text; label.AccessibleName = text;
            return true;
        }
        internal static string StatusText(string provider, ProviderStatus state, UsageSnapshot data, string error, DateTimeOffset now)
        {
            if (provider == null) return UiText.T("Выберите провайдера в меню", "Choose a provider in the menu");
            string stale = UiText.T("Данные устарели.", "Data is out of date.") + (error == null ? "" : "\n" + error);
            if (state == ProviderStatus.Gone)
                return data == null ? UiText.T("нет данных", "not available") :
                    stale + "\n" + UiText.T("данные не поступают; провайдер неактивен", "no data is arriving; the provider is inactive");
            if (data == null)
                return error == null ? UiText.T("Обновление…", "Updating…") :
                    error + "\n" + UiText.T("нет данных", "not available");
            if (state == ProviderStatus.Stale || IsStale(data, error, now)) return stale;
            if (state == ProviderStatus.Offline)
                return provider == "OpenCode" ?
                    UiText.T("OpenCode не запущен; данные из локальной базы", "OpenCode is not running; data from the local database") :
                    provider + UiText.T(" не запущен; данные из сохранённого входа", " is not running; data from the saved sign-in");
            return "";
        }
        internal static bool IsStale(UsageSnapshot data, string error, DateTimeOffset now)
        {
            return data != null && (error != null || (now - data.ReceivedAtUtc).TotalSeconds >= 120);
        }
        internal static string LocalDate(DateTimeOffset value)
        {
            return TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        }
        internal static string Countdown(DateTimeOffset? reset, DateTimeOffset now)
        {
            if (!reset.HasValue) return UiText.T("нет данных", "not available");
            long minutes = (long)Math.Max(0, Math.Ceiling((reset.Value - now).TotalMinutes));
            long days = minutes / 1440;
            string word = days % 100 >= 11 && days % 100 <= 14 ? "дней" :
                days % 10 == 1 ? "день" : days % 10 >= 2 && days % 10 <= 4 ? "дня" : "дней";
            if (!UiText.Russian) word = days == 1 ? "day" : "days";
            return (days > 0 ? days.ToString(CultureInfo.InvariantCulture) + " " + word + " " : "") +
                (minutes % 1440 / 60).ToString(CultureInfo.InvariantCulture) + UiText.T(" ч ", " h ") +
                (minutes % 60).ToString(CultureInfo.InvariantCulture) + UiText.T(" мин", " min");
        }
        protected override bool ProcessCmdKey(ref Message message, Keys key)
        {
            if (key == Keys.Escape) { Hide(); return true; }
            return base.ProcessCmdKey(ref message, key);
        }
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x02E0 && Visible)
            {
                int newDpi = (int)(message.WParam.ToInt64() & 0xffff);
                Build(Screen.FromPoint(Location).WorkingArea, newDpi);
                Bounds = Place(anchor, Size, Screen.FromPoint(Location).WorkingArea);
            }
            else if (message.Msg == 0x001A || message.Msg == 0x0015 || message.Msg == 0x031A || message.Msg == 0x031E)
            {
                needsBuild = true;
                if (Visible) ShowCard(anchor, false);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                countdown.Dispose();
                if (normal != null) normal.Dispose();
                if (bold != null) bold.Dispose();
            }
            base.Dispose(disposing);
        }
    }
    internal sealed class TrayWindow : NativeWindow, IDisposable
    {
        internal const int CallbackMessage = 0x8001;
        internal readonly int TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        internal Action<int, Point> Callback { get; set; }
        internal Action SessionEnded { get; set; }
        internal Action DisplayChanged { get; set; }
        internal string Tip { get; set; }
        internal bool Registered { get; private set; }
        internal int RegistrationCount { get; private set; }
        private Icon icon;
        internal TrayWindow()
        {
            CreateHandle(new CreateParams { Caption = "AI Usage Tray message window",
                Style = unchecked((int)0x80000000), ExStyle = 0x80 });
            // Explorer may run at a lower integrity level. Allow only our two Shell messages.
            Native.ChangeWindowMessageFilterEx(Handle, CallbackMessage, 1, IntPtr.Zero);
            Native.ChangeWindowMessageFilterEx(Handle, (uint)TaskbarCreated, 1, IntPtr.Zero);
        }
        private Native.IconData Data()
        {
            return new Native.IconData { Size = (uint)Marshal.SizeOf(typeof(Native.IconData)),
                Window = Handle, Id = 1, Flags = 7, Callback = CallbackMessage,
                Icon = icon == null ? IntPtr.Zero : icon.Handle, Tip = Tip ?? String.Empty,
                Info = "", InfoTitle = ""
            };
        }
        internal void Update(Icon value)
        {
            icon = value;
            Native.IconData data = Data();
            if (Registered && Native.ShellNotify(1, ref data)) return;
            Restore();
        }
        internal void Restore()
        {
            if (icon == null) return;
            Native.IconData data = Data();
            Registered = Native.ShellNotify(0, ref data) || Native.ShellNotify(1, ref data);
            if (Registered)
            {
                data.Version = 4;
                Native.ShellNotify(4, ref data);
                RegistrationCount++;
            }
        }
        internal Rectangle IconBounds()
        {
            Native.IconIdentifier id = new Native.IconIdentifier { Size = (uint)Marshal.SizeOf(typeof(Native.IconIdentifier)), Window = Handle, Id = 1 };
            Native.Rect rect;
            if (Native.ShellGetRect(ref id, out rect) == 0) return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return Rectangle.Empty;
        }
        internal void ReturnFocus()
        {
            Native.IconData data = Data();
            Native.ShellNotify(3, ref data);
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == TaskbarCreated) { Registered = false; Restore(); if (DisplayChanged != null) DisplayChanged(); }
            else if (message.Msg == 0x001A || message.Msg == 0x031A || message.Msg == 0x007E || message.Msg == 0x02E0)
            {
                if (DisplayChanged != null) DisplayChanged();
            }
            else if (message.Msg == CallbackMessage)
            {
                long value = message.LParam.ToInt64();
                if (((value >> 16) & 0xffff) == 1 && Callback != null)
                {
                    long point = message.WParam.ToInt64();
                    Callback((int)(value & 0xffff), new Point((short)(point & 0xffff), (short)((point >> 16) & 0xffff)));
                }
                else if (message.WParam.ToInt64() == 1 && Callback != null)
                    Callback((int)value, Cursor.Position);
            }
            else if (message.Msg == 0x11) { message.Result = new IntPtr(1); return; }
            else if ((message.Msg == 0x10 || (message.Msg == 0x16 && message.WParam != IntPtr.Zero)) && SessionEnded != null)
            { SessionEnded(); return; }
            base.WndProc(ref message);
        }
        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            Native.IconData data = Data();
            Native.ShellNotify(2, ref data);
            Registered = false;
            DestroyHandle();
        }
    }
    internal sealed class TrayContext : ApplicationContext
    {
        private readonly IUsageProvider[] providers;
        private IUsageProvider provider;
        private CancellationTokenSource stop = new CancellationTokenSource();
        private readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 1 };
        private readonly System.Windows.Forms.Timer hover = new System.Windows.Forms.Timer { Interval = 100 };
        private readonly System.Windows.Forms.Timer open = new System.Windows.Forms.Timer { Interval = 500 };
        private readonly System.Windows.Forms.Timer redraw = new System.Windows.Forms.Timer { Interval = 200 };
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        internal readonly TrayWindow Tray = new TrayWindow();
        internal readonly PopupForm Popup;
        private Icon icon;
        private string signature;
        private UsageSnapshot snapshot;
        private string error;
        private int generation;
        private Task cycle = Task.FromResult<object>(null);
        private bool exiting;
        private DateTime? outsideSince;
        private Rectangle hoverAnchor;
        internal IUsageProvider[] Providers { get { return providers; } }
        internal IUsageProvider CurrentProvider { get { return provider; } }
        internal TrayContext(IUsageProvider[] registry, bool startPolling)
        {
            providers = registry;
            Popup = new PopupForm(null);
            Tray.Tip = TipFor(null);
            Tray.Callback = OnTray;
            Tray.SessionEnded = ExitThread;
            Tray.DisplayChanged = RedrawIcon;
            redraw.Tick += delegate { redraw.Stop(); if (!exiting) DrawIcon(); };
            foreach (IUsageProvider candidate in providers)
            {
                IUsageProvider next = candidate;
                ToolStripMenuItem item = new ToolStripMenuItem(next.Id, null, delegate { SwitchProvider(next); });
                item.Tag = next;
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(UiText.T("Выход", "Exit"), null, delegate { ExitThread(); });
            menu.Opening += delegate { RefreshProviderItems(); };
            menu.Closed += delegate { Tray.ReturnFocus(); };
            hover.Tick += delegate { CheckHover(DateTime.UtcNow); };
            open.Tick += delegate {
                if (!hoverAnchor.Contains(Cursor.Position) || menu.Visible) { open.Stop(); return; }
                open.Stop(); Popup.ShowCard(hoverAnchor, false);
            };
            poll.Tick += async delegate { poll.Interval = 60000; await Refresh(); };
            DrawIcon();
            if (Tray.Registered) DrawIcon(); // Recalculate against the registered icon's monitor.
            if (startPolling) poll.Start();
        }
        internal void SwitchProvider(IUsageProvider next)
        {
            if (next == null || next == provider) return;
            generation++;
            CancellationTokenSource previous = stop;
            stop = new CancellationTokenSource();
            previous.Cancel();
            previous.Dispose();
            provider = next;
            snapshot = null;
            error = null;
            signature = null;
            Popup.SetProvider(next.Id);
            Tray.Tip = TipFor(next.Id);
            DrawIcon();
            Popup.SetSnapshot(snapshot, error, CurrentStatus());
            Task prior = cycle;
            cycle = prior.IsCompleted ? ReadOnce() : WaitThenRead(prior);
        }
        private static string TipFor(string providerId)
        {
            if (providerId == null) return UiText.T("Выберите провайдера в меню", "Choose a provider in the menu");
            if (providerId == "OpenCode") return "OpenCode · " + UiText.T("бесплатная квота Zen", "Zen free quota");
            return providerId + " · " + UiText.T("сверху 5 ч, снизу неделя", "top: 5 hours, bottom: week");
        }
        private Task Refresh()
        {
            if (exiting) return cycle;
            if (provider == null)
            {
                IUsageProvider detected = DetectFirst();
                if (detected != null) SwitchProvider(detected);
                return cycle;
            }
            if (!cycle.IsCompleted) return cycle;
            cycle = ReadOnce();
            return cycle;
        }
        private IUsageProvider DetectFirst()
        {
            foreach (IUsageProvider candidate in providers)
            {
                try { if (candidate.Detect() != ProviderAvailability.None) return candidate; }
                catch (Exception) { }
            }
            return null;
        }
        private async Task ReadOnce()
        {
            int current = generation;
            IUsageProvider source = provider;
            try
            {
                UsageSnapshot result = await source.ReadAsync(stop.Token, 15000);
                if (!exiting && current == generation) { snapshot = result; error = null; }
            }
            catch (OperationCanceledException) { }
            catch (UsageReadException exception) { if (!exiting && current == generation) error = exception.Message; }
            catch (Exception) { if (!exiting && current == generation) error = ReadFailureText(source.Id); }
            finally
            {
                if (!exiting && current == generation)
                {
                    DrawIcon();
                    Popup.SetSnapshot(snapshot, error, CurrentStatus());
                }
            }
        }
        private async Task WaitThenRead(Task prior)
        {
            try { await prior; }
            catch (Exception) { }
            if (!exiting && provider != null) await ReadOnce();
        }
        internal static string ReadFailureText(string providerId)
        {
            return UiText.T("Не удалось прочитать данные " + providerId, "Unable to read " + providerId + " data");
        }
        private ProviderStatus CurrentStatus()
        {
            IUsageProvider source = provider;
            if (source == null) return ProviderStatus.NoData;
            try { return source.Status; }
            catch (Exception) { return ProviderStatus.NoData; }
        }
        internal void SetSnapshot(UsageSnapshot value, string failure)
        {
            snapshot = value;
            error = failure;
            DrawIcon();
            Popup.SetSnapshot(snapshot, error, CurrentStatus());
        }
        private void DrawIcon()
        {
            Rectangle rect = Tray.IconBounds();
            int dpi = Native.DpiAt(rect.Location);
            int pixels = Math.Max(16, 16 * dpi / 96);
            string next;
            Icon replacement;
            if (provider != null && provider.Id == "OpenCode")
            {
                bool dark = Native.TrayDark;
                next = "opencode:" + pixels + ":" + (dark ? "dark" : "light");
                if (next == signature)
                {
                    if (!Tray.Registered) Tray.Restore();
                    return;
                }
                replacement = OpenCodeIcon.Get(pixels, dark);
            }
            else
            {
                double? five = snapshot == null ? null : snapshot.FiveHour.Remaining;
                double? week = snapshot == null ? null : snapshot.Weekly.Remaining;
                next = TrayIconRenderer.FormatPercent(five) + ":" + TrayIconRenderer.ColorFor(five).ToArgb() + "/" +
                    TrayIconRenderer.FormatPercent(week) + ":" + TrayIconRenderer.ColorFor(week).ToArgb() + "/" + pixels;
                if (next == signature)
                {
                    if (!Tray.Registered) Tray.Restore();
                    return;
                }
                replacement = TrayIconRenderer.Create(five, week, pixels);
            }
            Tray.Update(replacement);
            if (icon != null) icon.Dispose();
            icon = replacement;
            signature = next;
        }
        private void RedrawIcon()
        {
            if (exiting) return;
            signature = null;
            if (provider != null && provider.Id == "OpenCode") OpenCodeIcon.ClearCache();
            redraw.Stop();
            redraw.Start();
        }
        private void OnTray(int code, Point point)
        {
            if (code == 0x406 || code == 0x200 || code == 0x400 || code == 0x401 || code == 0x202)
            {
                DrawIcon();
                Rectangle anchor = Anchor();
                if (Popup.Visible) Popup.ShowCard(anchor, code == 0x400 || code == 0x401 || code == 0x202);
                else { open.Stop(); open.Start(); }
                outsideSince = null;
                hover.Start();
            }
            else if (code == 0x7b || code == 0x205)
            {
                open.Stop();
                Popup.ShowCard(Anchor(), true);
                hover.Start();
                menu.Show(point.X == -1 ? Cursor.Position : point);
            }
        }
        private void RefreshProviderItems()
        {
            foreach (ToolStripItem entry in menu.Items)
            {
                ToolStripMenuItem item = entry as ToolStripMenuItem;
                IUsageProvider candidate = entry.Tag as IUsageProvider;
                if (item == null || candidate == null) continue;
                bool detected = false;
                try { detected = candidate.Detect() != ProviderAvailability.None; }
                catch (Exception) { }
                item.Enabled = detected;
                item.Checked = candidate == provider;
            }
        }
        internal void CheckHover(DateTime now)
        {
            if (!Popup.Visible) {
                if (!open.Enabled || !hoverAnchor.Contains(Cursor.Position)) { open.Stop(); hover.Stop(); }
                outsideSince = null; return;
            }
            if (menu.Visible || Popup.Bounds.Contains(Cursor.Position) || hoverAnchor.Contains(Cursor.Position))
            { outsideSince = null; return; }
            if (!outsideSince.HasValue) outsideSince = now;
            if ((now - outsideSince.Value).TotalMilliseconds >= 500) { Popup.Hide(); hover.Stop(); outsideSince = null; }
        }
        private Rectangle Anchor()
        {
            Rectangle bounds = Tray.IconBounds();
            hoverAnchor = bounds.IsEmpty ? new Rectangle(Cursor.Position, new Size(1, 1)) : bounds;
            return hoverAnchor;
        }
        protected override void ExitThreadCore()
        {
            if (exiting) return;
            exiting = true;
            poll.Stop(); hover.Stop(); open.Stop(); redraw.Stop();
            stop.Cancel();
            foreach (IUsageProvider candidate in providers) candidate.Dispose();
            Tray.Dispose();
            OpenCodeIcon.ClearCache();
            Popup.Dispose();
            menu.Dispose();
            if (icon != null) icon.Dispose();
            poll.Dispose(); hover.Dispose(); open.Dispose(); redraw.Dispose();
            base.ExitThreadCore();
        }
    }
    internal enum ProviderAvailability { None, Offline, Active }
    internal enum ProviderStatus { Active, Offline, NoData, Stale, Gone }
    internal interface IUsageProvider : IDisposable
    {
        string Id { get; }
        int Priority { get; }
        ProviderAvailability Detect();
        ProviderStatus Status { get; }
        Task<UsageSnapshot> ReadAsync(CancellationToken stop, int timeoutMilliseconds);
    }
    internal sealed class CodexProvider : IUsageProvider
    {
        private readonly CodexReader reader = new CodexReader();
        private UsageSnapshot snapshot;
        private string error;
        public string Id { get { return "Codex"; } }
        public int Priority { get { return 1; } }
        public ProviderStatus Status
        {
            get { return ClassifyStatus(Detect(), snapshot, error, DateTimeOffset.UtcNow); }
        }
        public ProviderAvailability Detect()
        {
            return Classify(UserProcessRunning(), SavedLoginPresent());
        }
        public async Task<UsageSnapshot> ReadAsync(CancellationToken stop, int timeoutMilliseconds)
        {
            try
            {
                UsageSnapshot result = await reader.ReadAsync(null, stop, timeoutMilliseconds);
                snapshot = result;
                error = null;
                return result;
            }
            catch (UsageReadException exception)
            {
                error = exception.Message;
                throw;
            }
        }
        internal static ProviderAvailability Classify(bool processRunning, bool savedLogin)
        {
            if (processRunning) return ProviderAvailability.Active;
            if (savedLogin) return ProviderAvailability.Offline;
            return ProviderAvailability.None;
        }
        internal static ProviderStatus ClassifyStatus(ProviderAvailability availability, UsageSnapshot data, string failure, DateTimeOffset now)
        {
            if (availability == ProviderAvailability.None) return ProviderStatus.Gone;
            if (PopupForm.IsStale(data, failure, now)) return ProviderStatus.Stale;
            if (data == null) return ProviderStatus.NoData;
            return availability == ProviderAvailability.Active ? ProviderStatus.Active : ProviderStatus.Offline;
        }
        private bool UserProcessRunning()
        {
            int owned = reader.OwnedProcessId;
            bool found = false;
            try
            {
                foreach (Process process in Process.GetProcessesByName("codex"))
                {
                    try { found = found || process.Id != owned; }
                    catch (InvalidOperationException) { }
                    finally { process.Dispose(); }
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return found;
        }
        private static bool SavedLoginPresent()
        {
            try
            {
                string home = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (String.IsNullOrEmpty(home)) home = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                return File.Exists(Path.Combine(home, "auth.json"));
            }
            catch (ArgumentException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }
        public void Dispose() { reader.Dispose(); }
    }
    internal sealed class ClaudeCredentials
    {
        internal string AccessToken { get; set; }
        internal DateTimeOffset? ExpiresUtc { get; set; }
        internal string SubscriptionType { get; set; }
    }
    internal static class ClaudeFiles
    {
        internal static string DesktopConfigOverride = null;
        internal static string ConfigDirectory()
        {
            string configured = null;
            try { configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"); }
            catch (System.Security.SecurityException) { }
            if (!String.IsNullOrEmpty(configured)) return configured.Trim();
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }
        internal static string CredentialsPath()
        {
            return Path.Combine(ConfigDirectory(), ".credentials.json");
        }
        internal static string DesktopConfigPath()
        {
            if (!String.IsNullOrEmpty(DesktopConfigOverride)) return DesktopConfigOverride;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "config.json");
        }
        internal static bool CredentialsPresent()
        {
            return Exists(CredentialsPath());
        }
        internal static bool DesktopConfigPresent()
        {
            return Exists(DesktopConfigPath());
        }
        internal static bool DirectoryPresent()
        {
            try { return Directory.Exists(ConfigDirectory()) || Directory.Exists(Path.GetDirectoryName(DesktopConfigPath())); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }
        private static bool Exists(string path)
        {
            try { return File.Exists(path); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }
    }
    internal static class ClaudeCredentialsReader
    {
        internal static ClaudeCredentials ReadSaved()
        {
            ClaudeCredentials code = ReadClaudeCode();
            if (code != null) return code;
            return ReadClaudeDesktop();
        }
        private static ClaudeCredentials ReadClaudeCode()
        {
            try
            {
                FileInfo file = new FileInfo(ClaudeFiles.CredentialsPath());
                if (!file.Exists || file.Length > 1024 * 1024) return null;
                Dictionary<string, object> data = UsageParser.Json(File.ReadAllText(file.FullName, Encoding.UTF8));
                return FromMap(UsageParser.Map(UsageParser.Field(data, "claudeAiOauth")) ?? data);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (UsageReadException) { return null; }
        }
        private static ClaudeCredentials ReadClaudeDesktop()
        {
            try
            {
                FileInfo file = new FileInfo(ClaudeFiles.DesktopConfigPath());
                if (!file.Exists || file.Length > 8 * 1024 * 1024) return null;
                Dictionary<string, object> config = UsageParser.Json(File.ReadAllText(file.FullName, Encoding.UTF8));
                ClaudeCredentials cached = FromDesktopValue(UsageParser.Text(UsageParser.Field(config, "oauth:tokenCacheV2")));
                if (cached != null) return cached;
                return FromDesktopValue(UsageParser.Text(UsageParser.Field(config, "oauth:tokenCache")));
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (UsageReadException) { return null; }
        }
        internal static ClaudeCredentials FromDesktopValue(string raw)
        {
            if (String.IsNullOrEmpty(raw) || raw.Length > 256 * 1024) return null;
            string trimmed = raw.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("[", StringComparison.Ordinal))
                return trimmed.StartsWith("sk-ant-", StringComparison.Ordinal) ? Direct(trimmed) : null;
            try
            {
                object parsed = new JavaScriptSerializer { MaxJsonLength = UsageParser.MaxJsonLength, RecursionLimit = 64 }.DeserializeObject(raw);
                return FindTokened(parsed, 0, new int[] { 4096 });
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        private static ClaudeCredentials FindTokened(object node, int depth, int[] budget)
        {
            if (node == null || depth > 8 || --budget[0] < 0) return null;
            Dictionary<string, object> map = node as Dictionary<string, object>;
            if (map != null)
            {
                ClaudeCredentials direct = FromMap(map);
                if (direct != null) return direct;
                foreach (KeyValuePair<string, object> item in map)
                {
                    ClaudeCredentials nested = FindTokened(item.Value, depth + 1, budget);
                    if (nested != null) return nested;
                }
                return null;
            }
            object[] list = node as object[];
            if (list == null) return null;
            foreach (object item in list)
            {
                ClaudeCredentials nested = FindTokened(item, depth + 1, budget);
                if (nested != null) return nested;
            }
            return null;
        }
        private static ClaudeCredentials FromMap(Dictionary<string, object> map)
        {
            if (map == null) return null;
            string token = Token(UsageParser.Text(UsageParser.Field(map, "accessToken")) ??
                UsageParser.Text(UsageParser.Field(map, "access_token")));
            if (token == null) return null;
            return new ClaudeCredentials {
                AccessToken = token,
                ExpiresUtc = UsageParser.OptionalUnixUtc(UsageParser.Field(map, "expiresAt") ?? UsageParser.Field(map, "expires_at")),
                SubscriptionType = UsageParser.Text(UsageParser.Field(map, "subscriptionType")) ??
                    UsageParser.Text(UsageParser.Field(map, "subscription_type"))
            };
        }
        private static ClaudeCredentials Direct(string token)
        {
            string clean = Token(token);
            return clean == null ? null : new ClaudeCredentials { AccessToken = clean };
        }
        private static string Token(string value)
        {
            if (String.IsNullOrEmpty(value)) return null;
            string token = value.Trim();
            if (token.Length == 0 || token.Length > 4096) return null;
            foreach (char character in token)
                if (character < 0x21 || character > 0x7e) return null;
            return token;
        }
    }
    internal static class ClaudeUsageParser
    {
        internal static UsageSnapshot Parse(string json, ClaudeCredentials credentials, DateTimeOffset now)
        {
            Dictionary<string, object> envelope;
            try { envelope = UsageParser.Json(json); }
            catch (UsageReadException) { throw new UsageReadException(UiText.T("Некорректный ответ Claude", "Invalid response from Claude")); }
            string plan = UsageParser.Text(UsageParser.Field(envelope, "planType")) ??
                (credentials == null ? null : credentials.SubscriptionType);
            UsageSnapshot snapshot = new UsageSnapshot {
                FiveHour = Window(envelope, "five_hour"), Weekly = Window(envelope, "seven_day"),
                PlanType = plan,
                Credits = new List<ResetCredit>(), ReceivedAtUtc = now
            };
            if (snapshot.FiveHour.Remaining == null && snapshot.FiveHour.ResetUtc == null &&
                snapshot.Weekly.Remaining == null && snapshot.Weekly.ResetUtc == null)
                throw new UsageReadException(UiText.T("Сервис Claude вернул неизвестный формат", "Unexpected response format from Claude"));
            return snapshot;
        }
        internal static UsageWindow Window(Dictionary<string, object> envelope, string key)
        {
            Dictionary<string, object> window = UsageParser.Map(UsageParser.Field(envelope, key));
            UsageWindow result = new UsageWindow {
                Remaining = GetRemaining(UsageParser.Field(window, "utilization")),
                ResetUtc = UsageParser.OptionalDate(UsageParser.Field(window, "resets_at"))
            };
            if (!result.Remaining.HasValue)
                result.Remaining = GetRemainingPercent(UsageParser.Field(window, "used_percent") ?? UsageParser.Field(window, "usedPercent"));
            return result;
        }
        internal static double? GetRemaining(object utilization)
        {
            double? used = UsageParser.Number(utilization);
            if (!used.HasValue) return null;
            double percent = used.Value <= 1d ? used.Value * 100d : used.Value;
            return RemainingFromPercent(percent);
        }
        internal static double? GetRemainingPercent(object percentValue)
        {
            double? percent = UsageParser.Number(percentValue);
            return percent.HasValue ? RemainingFromPercent(percent.Value) : null;
        }
        private static double? RemainingFromPercent(double percent)
        {
            if (percent < 0d || percent > 100d) return null;
            return Math.Max(0d, Math.Min(100d, 100d - percent));
        }
    }
    internal sealed class ClaudeReader : IDisposable
    {
        internal const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
        internal const string UserAgent = "AIUsageTray/2.0.0";
        internal const int ResponseLimit = 1024 * 1024;
        internal const int TooManyRequestsBackoffMinutes = 5;
        private static readonly DateTime NoBackoff = DateTime.MinValue;
        private readonly object requestLock = new object();
        private HttpWebRequest active;
        private DateTime backoffUntilUtc = NoBackoff;
        private bool disposed;
        private int reading;
        static ClaudeReader()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        internal async Task<UsageSnapshot> ReadAsync(CancellationToken stop, int timeoutMilliseconds)
        {
            if (Interlocked.CompareExchange(ref reading, 1, 0) != 0)
                throw new UsageReadException(UiText.T("Чтение Claude уже выполняется", "A Claude read is already in progress"));
            try
            {
                using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    deadline.CancelAfter(timeoutMilliseconds);
                    return await Task.Run(delegate { return Read(deadline.Token, stop); }).ConfigureAwait(false);
                }
            }
            finally { Interlocked.Exchange(ref reading, 0); }
        }
        private UsageSnapshot Read(CancellationToken deadline, CancellationToken stop)
        {
            if (DateTime.UtcNow < backoffUntilUtc)
                throw new UsageReadException(CategoryFor(429));
            ClaudeCredentials credentials = ClaudeCredentialsReader.ReadSaved();
            if (credentials == null || String.IsNullOrEmpty(credentials.AccessToken))
                throw new UsageReadException(UiText.T("Сохранённый вход Claude не найден", "No saved Claude sign-in found"));
            if (credentials.ExpiresUtc.HasValue && credentials.ExpiresUtc.Value <= DateTimeOffset.UtcNow)
                throw new UsageReadException(UiText.T("Срок сохранённого входа Claude истёк", "The saved Claude sign-in has expired"));
            deadline.ThrowIfCancellationRequested();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(UsageUrl);
            request.Method = "GET";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.Accept = "application/json";
            request.UserAgent = UserAgent;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Headers["Authorization"] = "Bearer " + credentials.AccessToken;
            request.Headers["anthropic-beta"] = "oauth-2025-04-20";
            CancellationTokenRegistration cancel = new CancellationTokenRegistration();
            try
            {
                lock (requestLock)
                {
                    if (disposed) throw new OperationCanceledException(stop);
                    active = request;
                }
                cancel = deadline.Register(AbortActive);
                deadline.ThrowIfCancellationRequested();
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    StringBuilder body = new StringBuilder();
                    char[] buffer = new char[8192];
                    int count;
                    while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (body.Length + count > ResponseLimit)
                            throw new UsageReadException(UiText.T("Ответ Claude слишком большой", "Claude response is too large"));
                        body.Append(buffer, 0, count);
                    }
                    deadline.ThrowIfCancellationRequested();
                    return ClaudeUsageParser.Parse(body.ToString(), credentials, DateTimeOffset.UtcNow);
                }
            }
            catch (WebException exception)
            {
                if (stop.IsCancellationRequested) throw new OperationCanceledException(stop);
                HttpWebResponse response = exception.Response as HttpWebResponse;
                if (response != null)
                {
                    int status = (int)response.StatusCode;
                    response.Close();
                    if (status == 429) backoffUntilUtc = DateTime.UtcNow.AddMinutes(TooManyRequestsBackoffMinutes);
                    throw new UsageReadException(CategoryFor(status));
                }
                if (exception.Status == WebExceptionStatus.Timeout ||
                    (exception.Status == WebExceptionStatus.RequestCanceled && deadline.IsCancellationRequested))
                    throw new UsageReadException(UiText.T("Claude не ответил за 15 секунд", "Claude did not respond within 15 seconds"));
                throw new UsageReadException(UiText.T("Нет связи с сервисом Claude", "Cannot reach the Claude service"));
            }
            catch (IOException)
            {
                if (stop.IsCancellationRequested) throw new OperationCanceledException(stop);
                if (deadline.IsCancellationRequested)
                    throw new UsageReadException(UiText.T("Claude не ответил за 15 секунд", "Claude did not respond within 15 seconds"));
                throw new UsageReadException(UiText.T("Claude закрыл соединение", "Claude closed the connection"));
            }
            catch (OperationCanceledException)
            {
                if (stop.IsCancellationRequested) throw;
                throw new UsageReadException(UiText.T("Claude не ответил за 15 секунд", "Claude did not respond within 15 seconds"));
            }
            finally
            {
                cancel.Dispose();
                lock (requestLock) { if (active == request) active = null; }
            }
        }
        internal static string CategoryFor(int status)
        {
            if (status == 401) return UiText.T("Вход в Claude требует проверки (401)", "Claude sign-in needs attention (401)");
            if (status == 403) return UiText.T("Сервис Claude отказал в доступе (403)", "Claude service denied access (403)");
            if (status == 429) return UiText.T("Слишком много запросов к Claude (429)", "Too many requests to Claude (429)");
            return UiText.T("Ошибка сервиса Claude (" + status.ToString(CultureInfo.InvariantCulture) + ")",
                "Claude service error (" + status.ToString(CultureInfo.InvariantCulture) + ")");
        }
        private void AbortActive()
        {
            HttpWebRequest request;
            lock (requestLock) { request = active; }
            if (request == null) return;
            try { request.Abort(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        public void Dispose()
        {
            lock (requestLock) { disposed = true; }
            AbortActive();
        }
    }
    internal sealed class ClaudeProvider : IUsageProvider
    {
        private readonly ClaudeReader reader = new ClaudeReader();
        private UsageSnapshot snapshot;
        private string error;
        public string Id { get { return "Claude"; } }
        public int Priority { get { return 2; } }
        public ProviderStatus Status
        {
            get { return CodexProvider.ClassifyStatus(Detect(), snapshot, error, DateTimeOffset.UtcNow); }
        }
        public ProviderAvailability Detect()
        {
            return Classify(UserProcessRunning(), SavedSignInPresent());
        }
        public async Task<UsageSnapshot> ReadAsync(CancellationToken stop, int timeoutMilliseconds)
        {
            try
            {
                UsageSnapshot result = await reader.ReadAsync(stop, timeoutMilliseconds);
                snapshot = result;
                error = null;
                return result;
            }
            catch (UsageReadException exception)
            {
                error = exception.Message;
                throw;
            }
        }
        internal static ProviderAvailability Classify(bool processRunning, bool savedSignIn)
        {
            if (processRunning) return ProviderAvailability.Active;
            if (savedSignIn) return ProviderAvailability.Offline;
            return ProviderAvailability.None;
        }
        private static bool UserProcessRunning()
        {
            bool found = false;
            try
            {
                foreach (Process process in Process.GetProcessesByName("claude"))
                {
                    found = true;
                    process.Dispose();
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return found;
        }
        private static bool SavedSignInPresent()
        {
            return ClaudeFiles.CredentialsPresent() || ClaudeFiles.DesktopConfigPresent() || ClaudeFiles.DirectoryPresent();
        }
        public void Dispose() { reader.Dispose(); }
    }
    internal static class OpenCodeFiles
    {
        internal static string DataDirectory()
        {
            try
            {
                string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (!String.IsNullOrEmpty(xdg)) return Path.Combine(xdg.Trim(), "opencode");
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", "opencode");
            }
            catch (ArgumentException) { return null; }
            catch (System.Security.SecurityException) { return null; }
        }
        internal static string DatabasePath()
        {
            string directory = DataDirectory();
            if (String.IsNullOrEmpty(directory)) return null;
            try { return Path.Combine(directory, "opencode.db"); }
            catch (ArgumentException) { return null; }
        }
        internal static string AuthPath()
        {
            string directory = DataDirectory();
            if (String.IsNullOrEmpty(directory)) return null;
            try { return Path.Combine(directory, "auth.json"); }
            catch (ArgumentException) { return null; }
        }
        internal static bool DataPresent()
        {
            return Exists(DatabasePath()) || Exists(AuthPath());
        }
        private static bool Exists(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            try { return File.Exists(path); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }
    }
    internal static class OpenCodeLocator
    {
        private static string cached;
        internal static string Find(CancellationToken cancel)
        {
            if (!String.IsNullOrEmpty(cached) && File.Exists(cached)) return cached;
            foreach (string raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                cancel.ThrowIfCancellationRequested();
                string directory = raw.Trim().Trim('"');
                if (!CodexLocator.Absolute(directory)) continue;
                try
                {
                    string path = Path.Combine(directory, "opencode.exe");
                    if (File.Exists(path)) return cached = Path.GetFullPath(path);
                    bool shim = File.Exists(Path.Combine(directory, "opencode.cmd")) ||
                        File.Exists(Path.Combine(directory, "opencode.ps1"));
                    if (!shim) continue;
                    path = Path.Combine(directory, "node_modules", "opencode-ai", "bin", "opencode.exe");
                    if (File.Exists(path)) return cached = Path.GetFullPath(path);
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
            throw new UsageReadException(UiText.T("OpenCode CLI не найден", "The OpenCode CLI was not found"));
        }
        internal static void ClearCache() { cached = null; }
    }
    internal sealed class OpenCodeReader : IDisposable
    {
        private readonly object processLock = new object();
        private Process owned;
        private bool disposed;
        private bool cleanupFailed;
        private int reading;
        internal int OwnedProcessId
        {
            get { lock (processLock) { return owned == null ? 0 : owned.Id; } }
        }
        internal async Task<UsageSnapshot> ReadAsync(CancellationToken stop, int timeoutMilliseconds)
        {
            if (Interlocked.CompareExchange(ref reading, 1, 0) != 0)
                throw new UsageReadException(UiText.T("Чтение OpenCode уже выполняется", "An OpenCode read is already in progress"));
            try
            {
                using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    deadline.CancelAfter(timeoutMilliseconds);
                    return await Task.Run(delegate { return Read(deadline.Token, stop); }).ConfigureAwait(false);
                }
            }
            finally { Interlocked.Exchange(ref reading, 0); }
        }
        private UsageSnapshot Read(CancellationToken deadline, CancellationToken stop)
        {
            string database = OpenCodeFiles.DatabasePath();
            if (String.IsNullOrEmpty(database) || !File.Exists(database))
                throw new UsageReadException(UiText.T("Локальная база OpenCode не найдена", "The local OpenCode database was not found"));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            long start = OpenCodeUsageParser.StartOfUtcDay(now);
            string sql = OpenCodeUsageParser.Query(start, start + OpenCodeUsageParser.DayMilliseconds);
            Process process = null;
            Task errors = null;
            CancellationTokenRegistration cancel = new CancellationTokenRegistration();
            try
            {
                deadline.ThrowIfCancellationRequested();
                ProcessStartInfo launch = CodexLocator.Hidden(OpenCodeLocator.Find(deadline),
                    "db \"" + sql + "\" --format json --pure");
                process = new Process { StartInfo = launch };
                lock (processLock)
                {
                    if (disposed) throw new OperationCanceledException(stop);
                    if (cleanupFailed) throw new UsageReadException(UiText.T("Предыдущий процесс OpenCode не завершён", "The previous OpenCode process has not exited"));
                    process.Start();
                    owned = process;
                }
                cancel = deadline.Register(KillOwned);
                deadline.ThrowIfCancellationRequested();
                errors = Task.Run(delegate {
                    try { char[] buffer = new char[4096]; while (process.StandardError.Read(buffer, 0, buffer.Length) != 0) { } }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                });
                string output = ReadOutput(process, deadline);
                if (!process.WaitForExit(3000))
                    throw new UsageReadException(UiText.T("OpenCode CLI не завершился", "The OpenCode CLI did not exit"));
                deadline.ThrowIfCancellationRequested();
                stop.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new UsageReadException(UiText.T("OpenCode CLI завершился с ошибкой (код ", "The OpenCode CLI exited with an error (code ") +
                        process.ExitCode.ToString(CultureInfo.InvariantCulture) + ")");
                OpenCodeUsageReport report = OpenCodeUsageParser.Parse(output, now);
                return OpenCodeUsageParser.Snapshot(report, now);
            }
            catch (OperationCanceledException)
            {
                if (stop.IsCancellationRequested) throw;
                throw new UsageReadException(UiText.T("OpenCode не ответил за 15 секунд", "OpenCode did not respond within 15 seconds"));
            }
            catch (System.ComponentModel.Win32Exception)
            {
                OpenCodeLocator.ClearCache();
                throw new UsageReadException(UiText.T("Не удалось запустить OpenCode CLI", "Unable to start the OpenCode CLI"));
            }
            catch (IOException)
            {
                if (stop.IsCancellationRequested) throw new OperationCanceledException(stop);
                throw new UsageReadException(deadline.IsCancellationRequested ?
                    UiText.T("OpenCode не ответил за 15 секунд", "OpenCode did not respond within 15 seconds") :
                    UiText.T("OpenCode закрыл соединение", "OpenCode closed the connection"));
            }
            finally
            {
                cancel.Dispose();
                if (process != null)
                {
                    bool stopped = true;
                    try
                    {
                        try { process.StandardInput.Close(); } catch (IOException) { } catch (InvalidOperationException) { }
                        if (!process.WaitForExit(500)) CodexLocator.Stop(process);
                        stopped = process.HasExited;
                        if (errors != null) errors.Wait(500);
                    }
                    catch (InvalidOperationException) { stopped = true; }
                    finally
                    {
                        lock (processLock)
                        {
                            if (owned == process) owned = null;
                            cleanupFailed = !stopped;
                        }
                        process.Dispose();
                    }
                    if (cleanupFailed)
                        throw new UsageReadException(UiText.T("Не удалось завершить собственный процесс OpenCode", "Unable to stop the owned OpenCode process"));
                }
            }
        }
        private static string ReadOutput(Process process, CancellationToken deadline)
        {
            StringBuilder body = new StringBuilder();
            char[] buffer = new char[4096];
            while (true)
            {
                deadline.ThrowIfCancellationRequested();
                int count = process.StandardOutput.Read(buffer, 0, buffer.Length);
                deadline.ThrowIfCancellationRequested();
                if (count <= 0) break;
                if (body.Length + count > UsageParser.MaxJsonLength)
                    throw new UsageReadException(UiText.T("Ответ OpenCode слишком большой", "The OpenCode response is too large"));
                body.Append(buffer, 0, count);
            }
            return body.ToString();
        }
        private void KillOwned()
        {
            lock (processLock) { if (owned != null) CodexLocator.Stop(owned); }
        }
        public void Dispose()
        {
            lock (processLock) { disposed = true; }
            KillOwned();
        }
    }
    internal sealed class OpenCodeProvider : IUsageProvider
    {
        private readonly OpenCodeReader reader = new OpenCodeReader();
        private UsageSnapshot snapshot;
        private string error;
        internal ZenIpState IpState { get; set; }
        public string Id { get { return "OpenCode"; } }
        public int Priority { get { return 3; } }
        public ProviderStatus Status
        {
            get { return CodexProvider.ClassifyStatus(Detect(), snapshot, error, DateTimeOffset.UtcNow); }
        }
        public ProviderAvailability Detect()
        {
            return Classify(UserProcessRunning(), OpenCodeFiles.DataPresent());
        }
        public async Task<UsageSnapshot> ReadAsync(CancellationToken stop, int timeoutMilliseconds)
        {
            try
            {
                UsageSnapshot result = await reader.ReadAsync(stop, timeoutMilliseconds);
                if (IpState != ZenIpState.Unknown)
                    result.ZenQuota = ZenQuotaCalculator.Calculate(result.OpenCode, IpState, result.ReceivedAtUtc);
                snapshot = result;
                error = null;
                return result;
            }
            catch (UsageReadException exception)
            {
                error = exception.Message;
                throw;
            }
        }
        internal static ProviderAvailability Classify(bool processRunning, bool dataPresent)
        {
            if (processRunning) return ProviderAvailability.Active;
            if (dataPresent) return ProviderAvailability.Offline;
            return ProviderAvailability.None;
        }
        private bool UserProcessRunning()
        {
            int owned = reader.OwnedProcessId;
            bool found = false;
            try
            {
                foreach (Process process in Process.GetProcessesByName("opencode"))
                {
                    try { found = found || process.Id != owned; }
                    catch (InvalidOperationException) { }
                    finally { process.Dispose(); }
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return found;
        }
        public void Dispose() { reader.Dispose(); }
    }
    internal static class ProviderRegistry
    {
        internal static IUsageProvider[] Create()
        {
            return new IUsageProvider[] { new CodexProvider(), new ClaudeProvider(), new OpenCodeProvider() };
        }
    }
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Native.EnableDpi();
            if (Array.IndexOf(args, "--with-codex") >= 0) OpenCodex();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (TrayContext app = new TrayContext(ProviderRegistry.Create(), true)) Application.Run(app);
        }
        private static void OpenCodex()
        {
            try
            {
                using (Process launcher = Process.Start(new ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App") { UseShellExecute = true })) { }
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                MessageBox.Show(UiText.T("Не удалось открыть Codex:\n", "Could not open Codex:\n") + exception.Message,
                    "AI Usage Tray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
    internal static class Native
    {
        internal static bool PopupDark
        {
            get
            {
                if (SystemInformation.HighContrast) return false;
                try { return Object.Equals(Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1), 0); }
                catch (System.Security.SecurityException) { return false; }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
        }
        internal static bool TrayDark
        {
            get
            {
                if (SystemInformation.HighContrast) return DarkBackground(SystemColors.Window);
                try { return Object.Equals(Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 1), 0); }
                catch (System.Security.SecurityException) { return false; }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
        }
        internal static bool DarkBackground(Color background)
        {
            return background.R + background.G + background.B < 384;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Margins { internal int Left, Right, Top, Bottom; }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
        internal static void ApplyPopupFrame(IntPtr window)
        {
            try
            {
                int dark = PopupDark ? 1 : 0, corners = 2, border = -1;
                DwmSetWindowAttribute(window, 20, ref dark, 4);
                DwmSetWindowAttribute(window, 33, ref corners, 4);
                DwmSetWindowAttribute(window, 34, ref border, 4);
                Margins margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
                DwmExtendFrameIntoClientArea(window, ref margins);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] internal static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr info);
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShellNotify(uint operation, ref IconData data);
        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
        internal static extern int ShellGetRect(ref IconIdentifier id, out Rect rect);
        internal static void EnableDpi()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; }
            catch (EntryPointNotFoundException) { }
            SetProcessDPIAware();
        }
        internal static int DpiAt(Point point)
        {
            try { uint x, y; if (GetDpiForMonitor(MonitorFromPoint(point, 2), 0, out x, out y) == 0) return (int)x; }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return 96;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct IconData
        {
            internal uint Size;
            internal IntPtr Window;
            internal uint Id, Flags, Callback;
            internal IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
            internal uint State, StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
            internal uint Version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
            internal uint InfoFlags;
            internal Guid Guid;
            internal IntPtr BalloonIcon;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct IconIdentifier
        {
            internal uint Size;
            internal IntPtr Window;
            internal uint Id;
            internal Guid Guid;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { internal int Left, Top, Right, Bottom; }
    }
}

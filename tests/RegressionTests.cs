using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

internal static class RegressionTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static Assembly app;
    private static string root;
    private static string bin;
    private static string pidFile;
    private static int passed, failed;

    private static int Main(string[] args)
    {
        if (args.Length < 2) return 2;
        app = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        root = Path.GetFullPath(args[1]);
        bin = Path.Combine(root, "bin");
        pidFile = Path.Combine(root, "pids.txt");
        Environment.SetEnvironmentVariable("PATH", bin + ";" + Environment.GetEnvironmentVariable("PATH"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", root);
        Environment.SetEnvironmentVariable("TRAY_TEST_PIDS", pidFile);
        Directory.CreateDirectory(Path.Combine(root, "opencode"));
        File.WriteAllText(Path.Combine(root, "opencode", "opencode.db"), "synthetic fixture; never opened by a real CLI");
        bool hasClaude = app.GetType("AIUsageTray.ClaudeUsageParser") != null;
        if (hasClaude) Run("Claude percentage boundaries", Claude);
        foreach (string provider in hasClaude ? new[] { "Codex", "OpenCode" } : new[] { "Codex" })
        {
            string current = provider;
            Run(current + " timeout with inherited pipe", delegate { ReadCase(current, "hang", false); });
            Run(current + " cancellation with inherited pipe", delegate { ReadCase(current, "hang", true); });
            Run(current + " exited parent with inherited pipe", delegate { ReadCase(current, "orphan", false); });
            Run(current + " sanitized error", delegate { Error(current); });
        }
        Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
    private static void Run(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS: " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL: " + name + ": " + Unwrap(error).Message); }
        finally { Cleanup(); }
    }
    private static Type Type(string name) { return app.GetType("AIUsageTray." + name, true); }
    private static object New(string name) { return Activator.CreateInstance(Type(name), true); }
    private static object Call(object target, string name, params object[] args)
    {
        Type type = target as Type ?? target.GetType();
        foreach (MethodInfo method in type.GetMethods(Flags))
            if (method.Name == name && method.GetParameters().Length == args.Length)
                return method.Invoke(target is Type ? null : target, args);
        throw new MissingMethodException(type.Name, name);
    }
    private static Exception Unwrap(Exception error)
    {
        while (error.InnerException != null && (error is TargetInvocationException || error is AggregateException)) error = error.InnerException;
        return error;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Claude()
    {
        double[] used = { 0, 0.5, 1, 1.01, 100 };
        foreach (double value in used)
        {
            object result = Call(Type("ClaudeUsageParser"), "GetRemaining", value);
            Check(result != null && Math.Abs((double)result - (100 - value)) < 0.000001,
                value.ToString(CultureInfo.InvariantCulture) + " used returned " + Convert.ToString(result, CultureInfo.InvariantCulture));
            object snapshot = Call(Type("ClaudeUsageParser"), "Parse", "{\"five_hour\":{\"utilization\":" + value.ToString(CultureInfo.InvariantCulture) + "}}", null, DateTimeOffset.UtcNow);
            object window = snapshot.GetType().GetProperty("FiveHour", Flags).GetValue(snapshot, null);
            Check(Math.Abs((double)window.GetType().GetProperty("Remaining", Flags).GetValue(window, null) - (100 - value)) < 0.000001, "Claude JSON path disagrees");
        }
        foreach (object value in new object[] { -1d, 101d, Double.NaN, Double.PositiveInfinity, "invalid", null })
            Check(Call(Type("ClaudeUsageParser"), "GetRemaining", value) == null, "invalid utilization accepted");
    }
    private static Task Read(object reader, string provider, CancellationToken stop, int timeout)
    {
        if (provider == "Codex")
        {
            var start = (ProcessStartInfo)Call(Type("CodexLocator"), "Hidden", Path.Combine(bin, "codex.exe"), "app-server --listen stdio://");
            return (Task)Call(reader, "ReadAsync", start, stop, timeout);
        }
        return (Task)Call(reader, "ReadAsync", stop, timeout);
    }
    private static bool Complete(Task task, int timeout)
    {
        try { return task.Wait(timeout); }
        catch (AggregateException) { return true; }
    }
    private static void WaitForChild()
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000)
        {
            if (Pids().Length >= 2) return;
            Thread.Sleep(10);
        }
        throw new Exception("controlled child did not start");
    }
    private static void ReadCase(string provider, string mode, bool cancel)
    {
        File.WriteAllText(pidFile, "");
        Environment.SetEnvironmentVariable("TRAY_TEST_MODE", mode);
        object reader = New(provider + "Reader");
        Task pending = null;
        Process sentinel = Process.Start(new ProcessStartInfo(Path.Combine(bin, "codex.exe"), "--sentinel") { UseShellExecute = false, CreateNoWindow = true });
        using (CancellationTokenSource stop = new CancellationTokenSource())
        {
            try
            {
                pending = Read(reader, provider, stop.Token, cancel ? 10000 : 900);
                WaitForChild();
                Task duplicate = Read(reader, provider, CancellationToken.None, 3000);
                Check(Complete(duplicate, 500) && duplicate.IsFaulted &&
                    Unwrap(duplicate.Exception).GetType().Name == "UsageReadException", "parallel read was not rejected");
                Stopwatch watch = Stopwatch.StartNew();
                if (cancel) stop.Cancel();
                Check(Complete(pending, 3500), "read remained blocked after deadline/cancellation");
                Check(watch.ElapsedMilliseconds < 3500, "read exceeded bounded cleanup");
                Check(pending.IsCanceled || pending.IsFaulted, "blocked CLI was accepted as success");
                Exception error = pending.Exception == null ? null : Unwrap(pending.Exception);
                Check(cancel ? pending.IsCanceled || error is OperationCanceledException : error != null && error.GetType().Name == "UsageReadException", "wrong timeout/cancellation result");
                Check(!sentinel.HasExited, "unrelated process was stopped");
                foreach (int id in Pids()) Check(!Alive(id), "owned process survived cleanup: " + id);
                Environment.SetEnvironmentVariable("TRAY_TEST_MODE", "success");
                Task retry = Read(reader, provider, CancellationToken.None, 3000);
                Check(Complete(retry, 4000) && retry.Status == TaskStatus.RanToCompletion, "next read did not recover");
                object snapshot = retry.GetType().GetProperty("Result").GetValue(retry, null);
                if (provider == "OpenCode") Check((long)snapshot.GetType().GetProperty("TodayTokens", Flags).GetValue(snapshot, null) == 42, "retry lost OpenCode usage");
                else
                {
                    object window = snapshot.GetType().GetProperty("FiveHour", Flags).GetValue(snapshot, null);
                    Check((double)window.GetType().GetProperty("Remaining", Flags).GetValue(window, null) == 80, "retry lost Codex usage");
                }
            }
            finally
            {
                Cleanup();
                if (pending != null) Complete(pending, 2000);
                ((IDisposable)reader).Dispose();
                if (!sentinel.HasExited) sentinel.Kill();
                sentinel.WaitForExit(1000);
                sentinel.Dispose();
            }
        }
    }
    private static void Error(string provider)
    {
        File.WriteAllText(pidFile, "");
        Environment.SetEnvironmentVariable("TRAY_TEST_MODE", "error");
        using (IDisposable reader = (IDisposable)New(provider + "Reader"))
        {
            Task task = Read(reader, provider, CancellationToken.None, 3000);
            Check(Complete(task, 4000) && task.IsFaulted, "error was not reported");
            string message = Unwrap(task.Exception).Message;
            Check(message.IndexOf("SYNTHETIC_SECRET", StringComparison.Ordinal) < 0, "secret appeared in error");
        }
    }
    private static int[] Pids()
    {
        if (!File.Exists(pidFile)) return new int[0];
        try
        {
            List<int> ids = new List<int>();
            using (FileStream stream = new FileStream(pidFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int id;
                    if (Int32.TryParse(line, out id)) ids.Add(id);
                }
            }
            return ids.ToArray();
        }
        catch (IOException) { return new int[0]; }
    }
    private static bool Alive(int id)
    {
        try { using (Process process = Process.GetProcessById(id)) return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static void Cleanup()
    {
        foreach (int id in Pids())
        {
            try
            {
                using (Process process = Process.GetProcessById(id))
                {
                    string path = process.MainModule.FileName;
                    if (!process.HasExited && (String.Equals(path, Path.Combine(bin, "codex.exe"), StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(path, Path.Combine(bin, "opencode.exe"), StringComparison.OrdinalIgnoreCase)))
                    { process.Kill(); process.WaitForExit(1000); }
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
    }
}

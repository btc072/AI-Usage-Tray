using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal static class OpenCodeStub
{
    private static int Main(string[] args)
    {
        string pidFile = Environment.GetEnvironmentVariable("T7_PID_FILE");
        if (!String.IsNullOrEmpty(pidFile)) File.AppendAllText(pidFile, Process.GetCurrentProcess().Id + "\n");
        if (Environment.GetEnvironmentVariable("T7_CHILD") == "1")
        {
            Thread.Sleep(200);
            StartChild(pidFile, Environment.GetEnvironmentVariable("T7_EXIT_EARLY") != "1");
            if (Environment.GetEnvironmentVariable("T7_EXIT_EARLY") == "1") return 0;
        }
        int delay;
        if (Int32.TryParse(Environment.GetEnvironmentVariable("T7_DELAY_MS"), out delay) && delay > 0)
            Thread.Sleep(delay);

        string database = Environment.GetEnvironmentVariable("T7_DB");
        string sqlite = Environment.GetEnvironmentVariable("T7_SQLITE");
        if (!String.IsNullOrEmpty(database) && !String.IsNullOrEmpty(sqlite) && args.Length > 1)
        {
            ProcessStartInfo start = new ProcessStartInfo(sqlite,
                "-json " + Quote(database) + " " + Quote(args[1]));
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                Console.Out.Write(output);
                return process.ExitCode;
            }
        }

        string replay = Environment.GetEnvironmentVariable("T7_OUTPUT");
        Console.Out.Write(String.IsNullOrEmpty(replay) ? "[]" : File.ReadAllText(replay));
        return 0;
    }

    private static void StartChild(string pidFile, bool wait)
    {
        string shell = Environment.GetEnvironmentVariable("COMSPEC");
        if (String.IsNullOrEmpty(shell)) shell = "cmd.exe";
        ProcessStartInfo start = new ProcessStartInfo(shell, "/c ping -n 60 127.0.0.1 > nul");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        using (Process child = Process.Start(start))
        {
            if (!String.IsNullOrEmpty(pidFile)) File.AppendAllText(pidFile, child.Id + "\n");
            if (wait) child.WaitForExit();
        }
    }

    private static string Quote(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
    }
}

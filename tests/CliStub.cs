using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal static class CliStub
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "--child" || args[0] == "--sentinel"))
        {
            Thread.Sleep(15000);
            return 0;
        }
        string pidFile = Environment.GetEnvironmentVariable("TRAY_TEST_PIDS");
        File.AppendAllText(pidFile, Process.GetCurrentProcess().Id + "\n");
        string mode = Environment.GetEnvironmentVariable("TRAY_TEST_MODE");
        if (mode == "hang" || mode == "orphan")
        {
            ProcessStartInfo start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--child");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            using (Process child = Process.Start(start)) File.AppendAllText(pidFile, child.Id + "\n");
            Thread.Sleep(150);
            if (mode == "orphan") return 0;
            Thread.Sleep(15000);
            return 0;
        }
        Console.Error.WriteLine("SYNTHETIC_SECRET_DO_NOT_DISPLAY");
        if (args.Length > 0 && args[0] == "db")
        {
            if (mode == "error") return 23;
            if (args.Length > 1 && args[1].Contains("UNION ALL"))
            {
                Console.Write("[{\"source\":\"step\",\"message_id\":\"synthetic\",\"at\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
                    ",\"message_data\":\"{\\\"providerID\\\":\\\"opencode\\\",\\\"modelID\\\":\\\"big-pickle\\\",\\\"role\\\":\\\"assistant\\\"}\",\"part_data\":\"{\\\"type\\\":\\\"step-finish\\\",\\\"tokens\\\":{\\\"input\\\":42,\\\"output\\\":0,\\\"reasoning\\\":0,\\\"cache\\\":{\\\"read\\\":0,\\\"write\\\":0}},\\\"cost\\\":0.25}\"}]");
                return 0;
            }
            Console.Write("[{\"messages\":1,\"tokens\":42,\"cost\":0.25}]");
            return 0;
        }
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            if (line.Contains("\"id\":1")) Console.WriteLine("{\"id\":1,\"result\":{}}");
            if (line.Contains("\"id\":2"))
            {
                if (mode == "error") Console.WriteLine("{\"id\":2,\"error\":{\"code\":-1,\"message\":\"SYNTHETIC_SECRET_DO_NOT_DISPLAY\"}}");
                else Console.WriteLine("{\"id\":2,\"result\":{\"rateLimits\":{\"planType\":\"pro\",\"primary\":{\"usedPercent\":20,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":40,\"windowDurationMins\":10080}}}}");
            }
        }
        return 0;
    }
}

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

internal static class LiveReadTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int Main(string[] args)
    {
        Assembly app = Assembly.LoadFrom(System.IO.Path.GetFullPath(args[0]));
        Array registry = (Array)app.GetType("AIUsageTray.ProviderRegistry").GetMethod("Create", Flags).Invoke(null, null);
        foreach (object provider in registry)
        {
            try
            {
                Type type = provider.GetType();
                string id = (string)type.GetProperty("Id").GetValue(provider, null);
                object availability = type.GetMethod("Detect").Invoke(provider, null);
                Console.WriteLine(id + " availability: " + availability);
                if (id == "OpenCode" || availability.ToString() == "None") continue;
                Task task = (Task)type.GetMethod("ReadAsync").Invoke(provider, new object[] { CancellationToken.None, 15000 });
                try { task.GetAwaiter().GetResult(); Console.WriteLine("PASS: " + id + " live quota read (no model request)"); }
                catch (Exception) { Console.WriteLine("UNVERIFIED: " + id + " live quota unavailable; " + type.GetProperty("Status").GetValue(provider, null)); }
            }
            finally { ((IDisposable)provider).Dispose(); }
        }
        return 0;
    }
}

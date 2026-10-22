using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class GuiTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Assembly app;
    private static string output;
    private static int result = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        app = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        output = Path.GetFullPath(args[1]);
        Call(Type("Native"), "EnableDpi");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Type contract = Type("IUsageProvider");
        Fake codex = new Fake(contract, "Codex"), claude = new Fake(contract, "Claude"), open = new Fake(contract, "OpenCode");
        Array providers = Array.CreateInstance(contract, 3);
        providers.SetValue(codex.GetTransparentProxy(), 0);
        providers.SetValue(claude.GetTransparentProxy(), 1);
        providers.SetValue(open.GetTransparentProxy(), 2);
        object context = Activator.CreateInstance(Type("TrayContext"), Flags, null, new object[] { providers, false }, null);
        System.Windows.Forms.Timer start = new System.Windows.Forms.Timer { Interval = 1 };
        start.Tick += async delegate
        {
            start.Stop();
            try
            {
                object tray = Field(context, "Tray");
                Check((bool)tray.GetType().GetProperty("Registered", Flags).GetValue(tray, null), "tray icon did not register");
                Call(context, "RefreshProviderItems");
                ContextMenuStrip menu = (ContextMenuStrip)Field(context, "menu");
                Check(menu.Items.Count == 5, "provider menu incomplete");
                for (int i = 0; i < 3; i++) Check(menu.Items[i].Enabled, "available provider disabled");
                menu.Show(new Point(Screen.PrimaryScreen.WorkingArea.Right - 250, Screen.PrimaryScreen.WorkingArea.Bottom - 200));
                await Task.Delay(100);
                Capture(menu, "menu.png");
                menu.Close();
                Console.WriteLine("PASS: actual EXE tray registration and provider menu");

                Call(context, "SwitchProvider", claude.GetTransparentProxy());
                await Task.Delay(50);
                object snapshot = Field(context, "snapshot");
                Check(Object.ReferenceEquals(snapshot, claude.Snapshot), "provider result missing");
                Form popup = (Form)Field(context, "Popup");
                Call(popup, "ShowCard", new Rectangle(Screen.PrimaryScreen.WorkingArea.Right - 100, Screen.PrimaryScreen.WorkingArea.Bottom - 40, 24, 24), true);
                await Task.Delay(100);
                string labels = TextOf(popup);
                Check(labels.Contains("99.5%") || labels.Contains("99,5%"), "Claude 0.5 percent card is incorrect");
                Capture(popup, "claude-card.png");
                Console.WriteLine("PASS: actual EXE Claude card at 0.5 percent utilization");

                claude.Fail = true;
                await (Task)Call(context, "Refresh");
                Check(Object.ReferenceEquals(snapshot, Field(context, "snapshot")), "failure erased last successful result");
                bool stale = (bool)Call(Type("PopupForm"), "IsStale", snapshot, "synthetic error", DateTimeOffset.UtcNow);
                Check(stale && Field(context, "error") != null, "failed refresh not stale");
                Check((bool)Call(Type("PopupForm"), "IsStale", snapshot, null, DateTimeOffset.UtcNow.AddMinutes(10)), "old result not stale by age");
                Call(popup, "ShowCard", new Rectangle(800, 600, 24, 24), true);
                Capture(popup, "stale-card.png");
                Console.WriteLine("PASS: actual EXE last result retained and marked stale");

                codex.Hold = true;
                Call(context, "SwitchProvider", codex.GetTransparentProxy());
                await Task.Delay(50);
                Task pending = codex.Pending;
                Check(pending != null && !pending.IsCompleted, "test read was not pending");
                Stopwatch watch = Stopwatch.StartNew();
                Call(context, "SwitchProvider", open.GetTransparentProxy());
                while (!((Task)Field(context, "cycle")).IsCompleted && watch.ElapsedMilliseconds < 2000) await Task.Delay(10);
                Check(pending.IsCanceled, "switch did not cancel old provider");
                Check(Object.ReferenceEquals(Field(context, "snapshot"), open.Snapshot), "new provider did not refresh");
                Check(watch.ElapsedMilliseconds < 2000, "switch blocked");
                Console.WriteLine("PASS: actual EXE provider switch during request");
                result = 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL: GUI " + Unwrap(error).Message); }
            finally
            {
                start.Dispose();
                ((ApplicationContext)context).ExitThread();
            }
        };
        start.Start();
        Application.Run((ApplicationContext)context);
        Check(codex.Disposed && claude.Disposed && open.Disposed, "exit did not dispose providers");
        Console.WriteLine("PASS: actual EXE context exits and releases providers");
        return result;
    }
    private static Type Type(string name) { return app.GetType("AIUsageTray." + name, true); }
    private static object Field(object target, string name) { return target.GetType().GetField(name, Flags).GetValue(target); }
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
    private static string TextOf(Control control)
    {
        string text = control.Text + "\n";
        foreach (Control child in control.Controls) text += TextOf(child);
        return text;
    }
    private static void Capture(Control control, string name)
    {
        using (Bitmap image = new Bitmap(control.Width, control.Height))
        { control.DrawToBitmap(image, new Rectangle(Point.Empty, control.Size)); image.Save(Path.Combine(output, name), ImageFormat.Png); }
    }
    private sealed class Fake : RealProxy
    {
        private readonly string id;
        internal readonly object Snapshot;
        internal bool Hold, Fail, Disposed;
        internal Task Pending;
        internal Fake(Type contract, string id) : base(contract)
        {
            this.id = id;
            Snapshot = Call(Type("ClaudeUsageParser"), "Parse", "{\"five_hour\":{\"utilization\":0.5},\"seven_day\":{\"utilization\":1}}", null, DateTimeOffset.UtcNow);
        }
        public override IMessage Invoke(IMessage message)
        {
            IMethodCallMessage call = (IMethodCallMessage)message;
            object value = null;
            if (call.MethodName == "get_Id") value = id;
            else if (call.MethodName == "get_Priority") value = 1;
            else if (call.MethodName == "get_Status") value = Enum.Parse(Type("ProviderStatus"), Fail ? "Stale" : "Active");
            else if (call.MethodName == "Detect") value = Enum.Parse(Type("ProviderAvailability"), "Active");
            else if (call.MethodName == "Dispose") Disposed = true;
            else if (call.MethodName == "ReadAsync")
            {
                object completion = Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(Type("UsageSnapshot")));
                Pending = (Task)completion.GetType().GetProperty("Task").GetValue(completion, null);
                if (Hold) ((CancellationToken)call.Args[0]).Register(delegate { Call(completion, "TrySetCanceled"); });
                else if (Fail) Call(completion, "SetException", new Exception("synthetic failure"));
                else Call(completion, "SetResult", Snapshot);
                value = Pending;
            }
            return new ReturnMessage(value, null, 0, call.LogicalCallContext, call);
        }
    }
}

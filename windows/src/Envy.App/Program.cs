using System.Runtime.InteropServices;
using Envy.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Envy.App;

public static class Program
{
    /// <summary>
    /// Starts Envy, or shows the copy that's already running (envy open, the Start menu, a second double-click).
    /// <c>--disconnect-all</c> takes Envy out of every shell and removes its instructions for coding agents, without a
    /// window; the uninstaller runs that.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--disconnect-all"))
        {
            // The uninstaller: out of every shell, and the instructions for coding agents removed.
            new EnvyStore(new AppPaths(), Environment.ProcessPath).Uninstall();
            return 0;
        }

        // One Envy per user (and per ENVY_HOME sandbox). A second start asks the first to show its window.
        var sandbox = Environment.GetEnvironmentVariable("ENVY_HOME") ?? "";
        var name = @"Local\Envy.Show." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sandbox.ToLowerInvariant())))[..16];
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, name, out var first);
        if (!first)
        {
            AllowSetForegroundWindow(-1);
            show.Set();
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) App.Log(ex); };
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            // What the XAML compiler's generated code does for an app with App.xaml.
            Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider.Initialize();
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            var app = new App(args.Contains("--background"));
            var waiter = new Thread(() =>
            {
                while (show.WaitOne()) dispatcher.TryEnqueue(app.ShowMainWindow);
            }) { IsBackground = true, Name = "Envy single instance" };
            waiter.Start();
        });
        return 0;
    }

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}

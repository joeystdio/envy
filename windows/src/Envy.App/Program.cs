using System.Runtime.InteropServices;
using Envy.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Envy.App;

public static class Program
{
    /// <summary>
    /// Starts Envy, or shows the copy that's already running (envy open, the Start menu, a second double-click).
    /// <c>--disconnect-all</c> takes Envy out of every shell without a window; the uninstaller runs that.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--disconnect-all"))
        {
            new EnvyStore(new AppPaths(), Environment.ProcessPath).DisconnectAll();
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

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
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

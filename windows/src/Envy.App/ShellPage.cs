using Envy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Envy.App;

/// <summary>Shell Integration: which shells load Envy, a check in a fresh shell, the envy command, and the generated files.</summary>
public sealed class ShellPage : UserControl
{
    readonly AppState state;
    readonly MainWindow window;
    readonly StackPanel body = new() { Spacing = 16, Padding = new Thickness(24, 16, 24, 24), MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Left };
    int previewShell;

    public ShellPage(AppState state, MainWindow window)
    {
        this.state = state;
        this.window = window;
        Content = new ScrollViewer { Content = body };
        Action<bool> onChanged = _ => Build();
        state.StatusChanged += Build;
        state.Changed += onChanged;
        Unloaded += (_, _) =>
        {
            state.StatusChanged -= Build;
            state.Changed -= onChanged;
        };
        Build();
        if (state.Store.Inspections.IsEmpty) state.InspectAll();
    }

    void Build()
    {
        var store = state.Store;
        body.Children.Clear();
        body.Children.Add(UI.Title("Shell Integration"));
        body.Children.Add(Status());
        body.Children.Add(UI.Subtitle("Shells"));
        foreach (var kind in Enum.GetValues<ShellKind>()) body.Children.Add(ShellCard(kind));

        body.Children.Add(UI.Subtitle("Coding agents"));
        var agents = state.Agents;
        body.Children.Add(UI.CardOf(UI.Text(state.Checking.Count > 0 && agents.Count == 0 ? "Looking…"
            : agents.Count == 0 ? "None found on the PATH of your shells." : UI.JoinList(agents) + " get what's switched on for the folder you start them in.")));

        body.Children.Add(UI.Subtitle("In a shell"));
        body.Children.Add(UI.CardOf(UI.Column(10,
            CommandLine("See what's loaded here", "envy status"),
            CommandLine("Reload right now", "envy reload"),
            CommandLine("Open this app", "envy open"),
            UI.Caption("Works in PowerShell, Git Bash and Command Prompt."))));

        body.Children.Add(UI.Subtitle("Generated files"));
        var preview = new ComboBox { Items = { "PowerShell (vars.ps1)", "Git Bash (vars.sh)", "Command Prompt (vars.cmd)" }, SelectedIndex = previewShell, MinWidth = 260 };
        var script = UI.Code(PreviewText());
        preview.SelectionChanged += (_, _) =>
        {
            previewShell = preview.SelectedIndex;
            ((TextBlock)script.Child).Text = PreviewText();
        };
        body.Children.Add(UI.CardOf(UI.Column(12,
            UI.Row(8, UI.Text("Location", bold: true), UI.Text(store.Paths.Abbreviate(store.Paths.ShellDirectory), mono: true, wrap: false),
                UI.Button("Show in File Explorer", () => UI.ShowInExplorer(store.Output.PowerShellHook))),
            UI.Caption("Only you can read this folder: the files hold your secret values so shells can load them."),
            UI.Row(8, UI.Text("Last updated", bold: true), UI.Text(store.LastWrite?.ToString("T") ?? "Not changed yet this session")),
            UI.Row(8, UI.Text("Preview (secrets masked)", bold: true), preview),
            new ScrollViewer { Content = script, MaxHeight = 320, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto })));
    }

    string PreviewText() => previewShell switch
    {
        1 => BashScript.Vars(state.Store.Config, maskSecrets: true, home: state.Store.Paths.Home),
        2 => CmdScript.Vars(state.Store.Config, state.Store.Paths.Home, maskSecrets: true),
        _ => PowerShellScript.Vars(state.Store.Config, maskSecrets: true, home: state.Store.Paths.Home),
    };

    UIElement Status()
    {
        var store = state.Store;
        var connected = store.Shells.Where(s => store.IsConnected(s.Kind)).Select(s => s.Name).ToList();
        var any = connected.Count > 0;
        var text = UI.Column(8,
            UI.Text(any ? "Your shells are connected" : "Connect your shells", 18, bold: true),
            UI.Text(any
                ? $"Every new {UI.JoinList(connected)} window loads the variables you switch on, and so does every coding agent you start from one. Open PowerShell and Git Bash windows pick up changes at their next prompt."
                : "Envy adds one line to each shell's startup file (and, for Command Prompt, to its AutoRun command), so every new window, and every coding agent you start from one, gets the variables you switch on.",
                color: UI.Secondary),
            UI.Row(8,
                any ? UI.Button("Check Again", state.InspectAll, glyph: UI.Refresh) : UI.Button("Connect All", ConnectAll, accent: true),
                UI.Button("Setup Assistant…", window.ShowSetupAssistant)));
        var row = UI.Row(16, UI.Icon(any ? UI.Verified : UI.Warning, 32, any ? UI.Success : UI.Caution), text);
        row.VerticalAlignment = VerticalAlignment.Top;
        return UI.CardOf(row, 20);
    }

    void ConnectAll()
    {
        foreach (var shell in state.Store.Shells) Attempt(() => state.Store.Connect(shell.Kind));
        state.InspectAll();
    }

    UIElement ShellCard(ShellKind kind)
    {
        var store = state.Store;
        var shell = store.Shells.FirstOrDefault(s => s.Kind == kind);
        var name = new InstalledShell(kind, "").Name;
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (shell is null)
        {
            grid.Children.Add(UI.Icon(UI.Terminal, 20, UI.Tertiary));
            var missing = UI.Column(2, UI.Text(name, bold: true), UI.Caption(kind == ShellKind.GitBash ? "Not installed. Git for Windows includes it." : "Not installed."));
            Grid.SetColumn(missing, 1);
            grid.Children.Add(missing);
            return UI.CardOf(grid);
        }

        var connected = store.IsConnected(kind);
        var icon = UI.Icon(connected ? UI.Check : UI.Terminal, 20, connected ? UI.Success : UI.Secondary);
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(icon);

        var details = UI.Column(4, UI.Text(name, bold: true));
        foreach (var target in store.Targets(kind)) details.Children.Add(UI.Text(store.Paths.Abbreviate(target), 12, UI.Secondary, mono: true));
        if (kind == ShellKind.Cmd)
            details.Children.Add(UI.Caption("Command Prompt has no prompt hook: it loads variables when a window opens, for the folder it opens in, and doesn't switch when you cd. Run envy reload after cd."));
        if (connected) details.Children.Add(Verification(kind, name));
        else details.Children.Add(UI.Caption("Connecting adds this, and keeps a copy of the previous file as …envy-backup:\n" + store.ConnectLine(kind)));
        Grid.SetColumn(details, 1);
        grid.Children.Add(details);

        var action = connected
            ? UI.Button("Disconnect", () => Attempt(() => store.Disconnect(kind)))
            : UI.Button("Connect", () => { Attempt(() => store.Connect(kind)); state.Inspect(kind); }, accent: true);
        action.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);
        return UI.CardOf(grid);
    }

    /// <summary>Whether a freshly opened shell of this kind loads Envy.</summary>
    UIElement Verification(ShellKind kind, string name)
    {
        if (state.Checking.Contains(kind))
            return UI.Row(8, new ProgressRing { IsActive = true, Width = 14, Height = 14 }, UI.Caption($"Opening a new {name} to check that it loads Envy…"));
        if (!state.Store.Inspections.TryGetValue(kind, out var inspection))
            return UI.Row(8, UI.Caption("Not tested yet."), UI.Link("Check", () => state.Inspect(kind)));
        if (inspection is null)
            return UI.Row(8, UI.Caption($"Couldn't check: {name} took too long to start. Open a new window and run envy status."), UI.Link("Check Again", () => state.Inspect(kind)));
        if (inspection.HookLoaded)
            return UI.Row(8, UI.Icon(UI.Verified, 14, UI.Success), UI.Text($"Verified: a new {name} loads Envy.", 12, UI.Success), UI.Link("Check Again", () => state.Inspect(kind)));
        if (inspection.ScriptsBlocked)
        {
            return UI.Column(6,
                UI.Row(8, UI.Icon(UI.Warning, 14, UI.Caution), UI.Text($"{name} doesn't run profile scripts here (execution policy {inspection.ExecutionPolicy}), so it can't load Envy.", 12, UI.Caution)),
                UI.Row(8, UI.Button("Allow My Profile Scripts", () => AllowScripts(kind)), UI.Caption("Sets the policy to RemoteSigned for your account only, as Windows 11's developer settings do.")));
        }
        return UI.Row(8, UI.Icon(UI.Warning, 14, UI.Caution),
            UI.Text($"A new {name} didn't load Envy. Something earlier in its startup may stop it, or your terminal starts it without the profile.", 12, UI.Caution),
            UI.Link("Check Again", () => state.Inspect(kind)));
    }

    void AllowScripts(ShellKind kind)
    {
        if (state.Store.Shells.FirstOrDefault(s => s.Kind == kind) is not { } shell) return;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(shell.Executable,
                "-NoLogo -NoProfile -NonInteractive -Command Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned -Force")
            { CreateNoWindow = true, UseShellExecute = false });
            process?.WaitForExit(15000);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { App.Log(e); }
        state.Inspect(kind);
    }

    static UIElement CommandLine(string label, string command)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(UI.Text(label));
        var copy = UI.CopyableCommand(command);
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        return grid;
    }

    async void Attempt(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (state.XamlRoot is { } root) await UI.Alert(root, "Couldn't change the file", e.Message);
        }
    }
}

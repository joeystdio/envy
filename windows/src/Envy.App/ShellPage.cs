using Envy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Envy.App;

/// <summary>
/// Shell Integration: which shells load Envy, a check in a fresh shell, instructions for coding agents, the envy command,
/// and the generated files.
/// </summary>
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
        var agentCard = UI.Column(14, UI.Text(state.Checking.Count > 0 && agents.Count == 0 ? "Looking for agents on your shells' PATH…"
            : agents.Count == 0 ? "None found on the PATH of your shells." : UI.JoinList(agents) + " get what's switched on for the folder you start them in."));
        foreach (var target in store.AgentTargets) agentCard.Children.Add(AgentRow(target));
        agentCard.Children.Add(UI.Caption("Teaches agents the envy command: where your variables come from, how to ask you for a secret without seeing it, and how to load a change. Claude Code reads it as a rule in every session; the other agents share one skill folder and load it when a task needs it. Envy writes only these files, never your own instructions."));
        body.Children.Add(UI.CardOf(agentCard));

        body.Children.Add(UI.Subtitle("In a shell"));
        body.Children.Add(UI.CardOf(UI.Column(10,
            CommandLine("See what's loaded here", "envy status"),
            CommandLine("Reload right now", "envy reload"),
            CommandLine("Everything else", "envy help"),
            UI.Caption(File.Exists(store.Output.Command)
                ? $"Works in PowerShell, Git Bash and Command Prompt. Programs started from a connected shell, coding agents included, can run envy too: it's {store.Paths.Abbreviate(store.Output.Command)}, which connected shells put on PATH."
                : "Works in PowerShell, Git Bash and Command Prompt."))));

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
        return UI.CardOf(UI.WrapRow(UI.Icon(any ? UI.Verified : UI.Warning, 32, any ? UI.Success : UI.Caution), text, spacing: 16), 20);
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
            return UI.WrapRow(null, UI.Caption($"Couldn't check: {name} took too long to start. Open a new window and run envy status."), UI.Link("Check Again", () => state.Inspect(kind)));
        if (inspection.HookLoaded)
            return UI.Row(8, UI.Icon(UI.Verified, 14, UI.Success), UI.Text($"Verified: a new {name} loads Envy.", 12, UI.Success), UI.Link("Check Again", () => state.Inspect(kind)));
        if (inspection.ScriptsBlocked)
        {
            return UI.Column(6,
                UI.WrapRow(UI.Icon(UI.Warning, 14, UI.Caution), UI.Text($"{name} doesn't run profile scripts here (execution policy {inspection.ExecutionPolicy}), so it can't load Envy.", 12, UI.Caution)),
                UI.WrapRow(UI.Button("Allow My Profile Scripts", () => AllowScripts(kind)), UI.Caption("Sets the policy to RemoteSigned for your account only, as Windows 11's developer settings do.")));
        }
        return UI.WrapRow(UI.Icon(UI.Warning, 14, UI.Caution),
            UI.Text($"A new {name} didn't load Envy. Something earlier in its startup may stop it, or your terminal starts it without the profile.", 12, UI.Caution),
            UI.Link("Check Again", () => state.Inspect(kind)));
    }

    void AllowScripts(ShellKind kind)
    {
        if (state.Store.Shells.FirstOrDefault(s => s.Kind == kind) is not { } shell) return;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(shell.Executable,
                "-NoLogo -NoProfile -NonInteractive -Command Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned -Force")
            { CreateNoWindow = true, UseShellExecute = false };
            ShellProbe.UseDefaultModulePath(start, kind);
            using var process = System.Diagnostics.Process.Start(start);
            process?.WaitForExit(15000);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { App.Log(e); }
        state.Inspect(kind);
    }

    UIElement AgentRow(AgentTarget target)
    {
        var store = state.Store;
        var status = AgentInstructions.Status(target);
        var present = store.IsPresent(target);
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var (glyph, color) = status switch
        {
            AgentInstructionStatus.Installed => (UI.Verified, UI.Success),
            AgentInstructionStatus.Outdated => (UI.Refresh, UI.Caution),
            _ => (UI.Sparkle, UI.Secondary),
        };
        var icon = UI.Icon(glyph, 20, color);
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(icon);

        var details = UI.Column(2, UI.Text(target.Name, bold: true));
        if (target.Agents.Length > 1) details.Children.Add(UI.Caption(UI.JoinList(target.Agents.Select(a => a.Name).ToList())));
        var where = present || status != AgentInstructionStatus.NotInstalled ? store.Paths.Abbreviate(target.File) : "Not found on this PC";
        var path = UI.Text(where, 12, UI.Secondary, mono: true);
        path.IsTextSelectionEnabled = true;
        details.Children.Add(path);
        if (status == AgentInstructionStatus.Outdated) details.Children.Add(UI.Caption("Written by an older Envy.", UI.Caution));
        if (status == AgentInstructionStatus.Foreign) details.Children.Add(UI.Caption("A file Envy didn't write is already there, so Envy leaves it alone.", UI.Caution));
        Grid.SetColumn(details, 1);
        grid.Children.Add(details);

        var actions = UI.Row(8);
        switch (status)
        {
            case AgentInstructionStatus.Installed:
                actions.Children.Add(UI.Button("Remove", () => Attempt(() => store.RemoveInstructions(target))));
                break;
            case AgentInstructionStatus.Outdated:
                actions.Children.Add(UI.Button("Remove", () => Attempt(() => store.RemoveInstructions(target))));
                actions.Children.Add(UI.Button("Update", () => Attempt(() => store.InstallInstructions([target])), accent: true));
                break;
            case AgentInstructionStatus.NotInstalled when present:
                actions.Children.Add(UI.Button("Install", () => Attempt(() => store.InstallInstructions([target])), accent: true));
                break;
        }
        actions.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return grid;
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

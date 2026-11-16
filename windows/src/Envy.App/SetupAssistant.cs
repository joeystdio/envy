using Envy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Envy.App;

/// <summary>
/// First-run Setup Assistant: shows the shells and coding agents it found, connects the shells and checks a fresh one of
/// each, offers to move plain-text secrets out of startup files and user environment variables into Credential
/// Manager, and imports projects from a folder of code.
/// </summary>
public sealed class SetupAssistant : Grid
{
    enum Step { Welcome, Connect, Secrets, Projects, Done }

    readonly AppState state;
    readonly MainWindow window;
    readonly Action close;
    readonly StackPanel dots = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
    readonly ScrollViewer page = new() { Padding = new Thickness(48, 20, 48, 20) };
    readonly Grid footer = new() { Padding = new Thickness(24, 14, 24, 14) };
    readonly HashSet<ShellKind> chosenShells;
    readonly List<PlaintextExport> exports;
    readonly HashSet<string> chosenExports;
    Step step = Step.Welcome;
    string? connectError;
    List<PlaintextExport>? importedExports;
    bool removedPlaintext;
    int? importedProjects;

    public SetupAssistant(AppState state, MainWindow window, Action close)
    {
        this.state = state;
        this.window = window;
        this.close = close;
        chosenShells = state.Store.Shells.Select(s => s.Kind).ToHashSet();
        exports = state.Store.PlaintextExports();
        chosenExports = exports.Where(e => e.Variable.IsSecret).Select(e => e.Id).ToHashSet();

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(dots);
        SetRow(page, 1);
        Children.Add(page);
        footer.BorderBrush = UI.CardStroke;
        footer.BorderThickness = new Thickness(0, 1, 0, 0);
        SetRow(footer, 2);
        Children.Add(footer);

        state.StatusChanged += Render;
        Unloaded += (_, _) => state.StatusChanged -= Render;
        if (state.Store.Inspections.IsEmpty) state.InspectAll();
        Render();
    }

    List<Step> Steps => [Step.Welcome, Step.Connect, .. exports.Count > 0 ? new[] { Step.Secrets } : [], Step.Projects, Step.Done];

    void Move(int offset)
    {
        var steps = Steps;
        step = steps[Math.Clamp(steps.IndexOf(step) + offset, 0, steps.Count - 1)];
        Render();
    }

    void Render()
    {
        dots.Children.Clear();
        foreach (var s in Steps)
        {
            dots.Children.Add(new Border
            {
                Width = s == step ? 20 : 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Background = s == step ? UI.Accent : UI.Brush("ControlStrongFillColorDisabledBrush"),
            });
        }
        var content = step switch
        {
            Step.Welcome => Welcome(),
            Step.Connect => Connect(),
            Step.Secrets => Secrets(),
            Step.Projects => Projects(),
            _ => Done(),
        };
        content.MaxWidth = 760;
        content.HorizontalAlignment = HorizontalAlignment.Left;
        page.Content = content;
        RenderFooter();
    }

    // Steps

    FrameworkElement Welcome()
    {
        var icon = new Image { Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Envy.ico"))), Width = 76, Height = 76 };
        var hello = UI.Row(16, icon, UI.Column(4, UI.Title("Welcome to Envy"),
            UI.Text("Secrets and environment variables for your shells, and for the coding agents you run in them.", color: UI.Secondary)));
        var features = UI.Column(12,
            Feature(UI.Layers, "Switch variables on and off", "Global and per-project sets. PowerShell and Git Bash follow at their next prompt."),
            Feature(UI.Folder, "Follows your folders", "A project linked to a folder loads when you cd in and unloads when you leave."),
            Feature(UI.Shield, "Secrets stay in Credential Manager", "Not in plain-text profiles or environment variables that every program, and every agent, can read."));

        var setup = UI.Column(8, UI.Text("Your setup", bold: true));
        foreach (var kind in Enum.GetValues<ShellKind>())
        {
            var shell = state.Store.Shells.FirstOrDefault(s => s.Kind == kind);
            var name = new InstalledShell(kind, "").Name;
            setup.Children.Add(Detected(UI.Terminal, name, shell is null ? "not installed" : state.Store.Paths.Abbreviate(shell.Executable) + (state.Store.IsConnected(kind) ? "  (connected)" : ""), shell is null));
        }
        var agents = state.Agents;
        setup.Children.Add(Detected(UI.Sparkle, "Coding agents",
            state.Checking.Count > 0 && agents.Count == 0 ? "Looking…" : agents.Count == 0 ? "None found on your PATH" : UI.JoinList(agents)));
        return UI.Column(24, hello, features, UI.CardOf(setup));
    }

    static UIElement Feature(string glyph, string title, string detail)
    {
        var icon = UI.Icon(glyph, 20, UI.Accent);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        return UI.Row(14, icon, UI.Column(2, UI.Text(title, bold: true), UI.Text(detail, color: UI.Secondary)));
    }

    static UIElement Detected(string glyph, string label, string value, bool dim = false)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(UI.Icon(glyph, 14, UI.Secondary));
        var l = UI.Text(label, color: UI.Secondary);
        SetColumn(l, 1);
        grid.Children.Add(l);
        var v = UI.Text(value, color: dim ? UI.Tertiary : null);
        v.IsTextSelectionEnabled = true;
        SetColumn(v, 2);
        grid.Children.Add(v);
        return grid;
    }

    bool ChosenConnected => chosenShells.Count > 0 && chosenShells.All(state.Store.IsConnected);

    FrameworkElement Connect()
    {
        var store = state.Store;
        var panel = UI.Column(14, UI.Title("Connect your shells"));
        if (store.Shells.Count == 0)
        {
            panel.Children.Add(UI.Text("Envy didn't find PowerShell, Git Bash or Command Prompt.", color: UI.Secondary));
            return panel;
        }
        if (ChosenConnected)
        {
            panel.Children.Add(UI.Row(8, UI.Icon(UI.Verified, 18, UI.Success), UI.Text("Connected", 16, UI.Success, bold: true)));
            foreach (var shell in store.Shells.Where(s => chosenShells.Contains(s.Kind))) panel.Children.Add(VerificationLine(shell));
            panel.Children.Add(UI.CardOf(UI.Column(6,
                UI.Text("Windows that are already open load Envy once you run this in them, or once you open a new one:"),
                UI.CopyableCommand(". $PROFILE"),
                UI.CopyableCommand("source ~/.bashrc"))));
            return panel;
        }

        panel.Children.Add(UI.Text("Envy adds a marked line to each startup file, so every new window loads the variables you switch on:"));
        foreach (var shell in store.Shells)
        {
            var kind = shell.Kind;
            var box = new CheckBox { IsChecked = chosenShells.Contains(kind), Content = UI.Column(2,
                UI.Text(shell.Name, bold: true),
                UI.Caption(string.Join(", ", store.Targets(kind).Select(store.Paths.Abbreviate)) + (store.IsConnected(kind) ? "  (connected)" : ""))) };
            box.Click += (_, _) => { if (box.IsChecked == true) chosenShells.Add(kind); else chosenShells.Remove(kind); RenderFooter(); };
            panel.Children.Add(box);
        }
        panel.Children.Add(UI.Code(string.Join("\n", store.Shells.Where(s => s.Kind != ShellKind.Cmd).Select(s => store.ConnectLine(s.Kind)).Distinct()
            .Prepend(StartupFiles.BeginMarker).Append(StartupFiles.EndMarker))));
        panel.Children.Add(UI.Column(6,
            UI.Row(8, UI.Icon(UI.Document, 14, UI.Secondary), UI.Text("A copy of each file is saved first, as …envy-backup.", color: UI.Secondary)),
            UI.Row(8, UI.Icon(UI.Close, 14, UI.Secondary), UI.Text("Disconnect any time from Shell Integration.", color: UI.Secondary)),
            UI.Row(8, UI.Icon(UI.Terminal, 14, UI.Secondary), UI.Text("Command Prompt runs Envy from its AutoRun command, after anything already there. It loads variables when a window opens and doesn't switch per folder.", color: UI.Secondary))));
        if (connectError is not null) panel.Children.Add(UI.Row(8, UI.Icon(UI.Warning, 14, UI.Critical), UI.Text(connectError, color: UI.Critical)));
        return panel;
    }

    UIElement VerificationLine(InstalledShell shell)
    {
        if (state.Checking.Contains(shell.Kind))
            return UI.Row(8, new ProgressRing { IsActive = true, Width = 14, Height = 14 }, UI.Text($"Opening a new {shell.Name} to check that it loads Envy…", color: UI.Secondary));
        if (!state.Store.Inspections.TryGetValue(shell.Kind, out var inspection) || inspection is null)
            return UI.Row(8, UI.Icon(UI.Info, 14, UI.Secondary), UI.Text($"{shell.Name}: couldn't check. Open a new window and run envy status.", color: UI.Secondary), UI.Link("Check Again", () => state.Inspect(shell.Kind)));
        if (inspection.HookLoaded)
            return UI.Row(8, UI.Icon(UI.Verified, 14, UI.Success), UI.Text($"Verified: a new {shell.Name} loads Envy.", color: UI.Success));
        var reason = inspection.ScriptsBlocked
            ? $"{shell.Name} doesn't run profile scripts (execution policy {inspection.ExecutionPolicy}). Allow them in Shell Integration."
            : $"A new {shell.Name} didn't load Envy.";
        return UI.Row(8, UI.Icon(UI.Warning, 14, UI.Caution), UI.Text(reason, color: UI.Caution), UI.Link("Check Again", () => state.Inspect(shell.Kind)));
    }

    FrameworkElement Secrets()
    {
        var panel = UI.Column(12, UI.Title("Move secrets out of plain text"));
        if (importedExports is { } imported)
        {
            panel.Children.Add(UI.Row(8, UI.Icon(UI.Verified, 18, UI.Success), UI.Text($"Imported {imported.Count} into Global.", 16, UI.Success, bold: true)));
            if (removedPlaintext)
            {
                panel.Children.Add(UI.Text("Removed them from where they were found. Each changed file was kept as …envy-backup; delete those once you've checked, since they still hold the values.", color: UI.Secondary));
            }
            else
            {
                panel.Children.Add(UI.Text("They're still where Envy found them too. Remove them there, so the secrets no longer sit in plain text:"));
                foreach (var group in imported.GroupBy(e => e.Source).OrderBy(g => g.Key))
                {
                    var where = group.First().IsUserEnvironment
                        ? $"User environment variables: {string.Join(", ", group.Select(e => e.Key))}"
                        : $"{state.Store.Paths.Abbreviate(group.Key)}, {(group.Count() == 1 ? "line" : "lines")} {string.Join(", ", group.Select(e => e.Line))}";
                    var row = UI.Row(8, UI.Text(where, mono: true));
                    if (!group.First().IsUserEnvironment)
                    {
                        var file = group.Key;
                        row.Children.Add(UI.Link("Open in Notepad", () => UI.OpenInNotepad(StartupFiles.Resolve(file))));
                    }
                    panel.Children.Add(row);
                }
                panel.Children.Add(UI.Button("Remove Them for Me", () =>
                {
                    try
                    {
                        state.Store.RemovePlaintext(imported);
                        removedPlaintext = true;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        connectError = e.Message;
                    }
                    Render();
                }, accent: true));
                panel.Children.Add(UI.Caption("Lines are deleted from the files (each keeps an …envy-backup). User environment variables are deleted, so programs started outside a shell, from the Start menu for example, no longer see them."));
            }
            return panel;
        }

        var secretCount = exports.Count(e => e.Variable.IsSecret);
        panel.Children.Add(UI.Text($"Your startup files and user environment variables hold {UI.Plural(exports.Count, "variable", "variables")} in plain text{(secretCount > 0 ? $", {secretCount} of which look like secrets" : "")}. Any program you run, coding agents included, can read them. Import them into Global and Envy keeps the secret ones in Credential Manager."));
        var list = new StackPanel { Spacing = 2 };
        foreach (var export in exports)
        {
            var source = export.IsUserEnvironment ? "user environment" : $"{state.Store.Paths.Abbreviate(export.Source)}:{export.Line}";
            var label = UI.Row(8, UI.Text(export.Key, mono: true, wrap: false));
            if (export.Variable.IsSecret) label.Children.Add(UI.Icon(UI.Lock, 12, UI.Secondary));
            label.Children.Add(UI.Text(export.Variable.IsSecret ? "••••••" : export.Value, color: UI.Secondary, wrap: false));
            label.Children.Add(UI.Text(source, 12, UI.Tertiary, wrap: false, mono: true));
            var box = new CheckBox { Content = label, IsChecked = chosenExports.Contains(export.Id) };
            var id = export.Id;
            box.Click += (_, _) => { if (box.IsChecked == true) chosenExports.Add(id); else chosenExports.Remove(id); RenderFooter(); };
            list.Children.Add(box);
        }
        panel.Children.Add(UI.CardOf(new ScrollViewer { Content = list, MaxHeight = 300 }, 8));
        return panel;
    }

    FrameworkElement Projects() => UI.Column(12,
        UI.Title("Bring in your projects"),
        UI.Text("Pick the folder where you keep your code. Envy looks for .env, .env.local and .envrc files, and for .NET appsettings.json and user secrets. Each folder it finds becomes a project that loads when a shell enters it.", color: UI.Secondary),
        new ProjectScanView(state, count => { importedProjects = count; RenderFooter(); }));

    FrameworkElement Done()
    {
        var connected = state.Store.AnyConnected;
        var panel = UI.Column(16,
            UI.Icon(UI.Verified, 60, UI.Success),
            UI.Title("You're set"),
            UI.Text(connected
                ? "Open a new PowerShell, Git Bash or Command Prompt window and run envy status to see what's loaded. Toggle anything from the sidebar or from Envy's icon in the notification area."
                : "No shell is connected yet. Connect them any time from Shell Integration.", color: UI.Secondary));
        if (connected) panel.Children.Add(UI.CopyableCommand("envy status"));
        foreach (var child in panel.Children.OfType<FrameworkElement>()) child.HorizontalAlignment = HorizontalAlignment.Center;
        foreach (var text in panel.Children.OfType<TextBlock>()) text.TextAlignment = TextAlignment.Center;
        panel.Margin = new Thickness(0, 60, 0, 0);
        panel.MaxWidth = 520;
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        return panel;
    }

    // Footer

    void RenderFooter()
    {
        footer.Children.Clear();
        footer.ColumnDefinitions.Clear();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (step == Step.Welcome) footer.Children.Add(UI.Button("Set Up Later", () => Finish()));
        else if (step != Step.Done) footer.Children.Add(UI.Button("Back", () => Move(-1)));

        var right = UI.Row(8);
        switch (step)
        {
            case Step.Welcome:
                right.Children.Add(UI.Button("Continue", () => Move(1), accent: true));
                break;
            case Step.Connect when ChosenConnected || state.Store.Shells.Count == 0:
                right.Children.Add(UI.Button("Continue", () => Move(1), accent: true));
                break;
            case Step.Connect:
                right.Children.Add(UI.Button("Skip", () => Move(1)));
                var connect = UI.Button("Connect", PerformConnect, accent: true);
                connect.IsEnabled = chosenShells.Count > 0;
                right.Children.Add(connect);
                break;
            case Step.Secrets when importedExports is null:
                right.Children.Add(UI.Button("Skip", () => Move(1)));
                var import = UI.Button(chosenExports.Count == 1 ? "Import 1 Variable" : $"Import {chosenExports.Count} Variables", ImportExports, accent: true);
                import.IsEnabled = chosenExports.Count > 0;
                right.Children.Add(import);
                break;
            case Step.Secrets:
                right.Children.Add(UI.Button("Continue", () => Move(1), accent: true));
                break;
            case Step.Projects:
                right.Children.Add(UI.Button(importedProjects is null ? "Skip" : "Continue", () => Move(1), accent: importedProjects is not null));
                break;
            case Step.Done:
                right.Children.Add(UI.Button("Add a Global Variable", () => Finish(() => _ = Dialogs.EditVariable(state, Project.GlobalId, null))));
                right.Children.Add(UI.Button("Start Using Envy", () => Finish(), accent: true));
                break;
        }
        SetColumn(right, 2);
        footer.Children.Add(right);
    }

    void PerformConnect()
    {
        connectError = null;
        foreach (var kind in chosenShells)
        {
            try { state.Store.Connect(kind); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { connectError = e.Message; }
        }
        foreach (var kind in chosenShells) state.Inspect(kind);
        Render();
    }

    void ImportExports()
    {
        var picked = exports.Where(e => chosenExports.Contains(e.Id)).ToList();
        state.Store.AddVariables(picked.Select(e => e.Variable), Project.GlobalId, MergePolicy.Replace, "Import from Startup Files");
        importedExports = picked;
        Render();
    }

    void Finish(Action? then = null)
    {
        state.Store.FinishOnboarding();
        close();
        window.Select(Project.GlobalId);
        then?.Invoke();
    }
}

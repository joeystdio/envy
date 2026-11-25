using Envy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Envy.App;

/// <summary>The sheets of the Mac app as dialogs: edit a variable, import, copy from a project, new project, find projects.</summary>
public static partial class Dialogs
{
    static bool open;

    /// <summary>
    /// Shows one dialog at a time; WinUI allows only one open ContentDialog. <paramref name="wait"/> waits for the open one
    /// to close instead of giving up, for dialogs the envy command asked for.
    /// </summary>
    static async Task<ContentDialogResult> Show(ContentDialog dialog, bool wait = false)
    {
        while (wait && open) await Task.Delay(250);
        if (open) return ContentDialogResult.None;
        open = true;
        try { return await dialog.ShowAsync(); }
        finally { open = false; }
    }

    // Variable editor

    /// <param name="template">Starting values for a new variable.</param>
    /// <param name="requested">Opened by the envy command for a coding agent: the value must be pasted before Add works,
    /// since the dialog can pop up while you're typing elsewhere, and a stray Enter shouldn't save it empty.</param>
    public static async Task EditVariable(AppState state, Guid projectId, Guid? variableId, EnvVar? template = null, bool requested = false)
    {
        if (state.XamlRoot is not { } root || state.Store.Config.Find(projectId) is not { } project) return;
        var isNew = variableId is null;
        var draft = variableId is { } id && project.Variables.Find(v => v.Id == id) is { } existing ? existing with { }
            : template is not null ? template with { Id = Guid.NewGuid() } : new EnvVar("");
        var secretTouched = !isNew || template is not null;
        Action validate = () => { };
        Control? valueBox = null;
        var reveal = false;

        var name = new TextBox { Header = "Name", PlaceholderText = "OPENAI_API_KEY", FontFamily = UI.Mono, Text = draft.Key };
        var nameHint = UI.Caption("");
        var valueHost = new Grid();
        var secret = new ToggleSwitch { Header = "Secret", OnContent = "Kept in Credential Manager instead of the settings file, and hidden in Envy.", OffContent = "Kept in the settings file and shown in Envy.", IsOn = draft.IsSecret };
        var expand = new ToggleSwitch { Header = "Expand variables", OnContent = "%NAME%, $NAME, ${NAME} and ~ are filled in when a shell loads it.", OffContent = "The value is used exactly as typed.", IsOn = draft.Expands };
        var note = new TextBox { Header = "Note", PlaceholderText = "Optional", Text = draft.Note };

        void BuildValue()
        {
            valueHost.Children.Clear();
            valueHost.ColumnDefinitions.Clear();
            valueHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            valueHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (draft.IsSecret && !reveal)
            {
                var box = new PasswordBox { Header = "Value", Password = draft.Value };
                box.PasswordChanged += (_, _) => { draft.Value = box.Password; validate(); };
                valueHost.Children.Add(box);
                valueBox = box;
            }
            else
            {
                // AcceptsReturn before Text: a single-line TextBox keeps only the first line of what it's given.
                var box = new TextBox { Header = "Value", AcceptsReturn = true, Text = draft.Value, FontFamily = UI.Mono, TextWrapping = TextWrapping.Wrap, MaxHeight = 180 };
                box.TextChanged += (_, _) => { draft.Value = box.Text; validate(); };
                valueHost.Children.Add(box);
                valueBox = box;
            }
            if (draft.IsSecret)
            {
                var eye = UI.IconButton(reveal ? UI.EyeOff : UI.Eye, reveal ? "Hide value" : "Show value", () => { reveal = !reveal; BuildValue(); });
                eye.VerticalAlignment = VerticalAlignment.Bottom;
                Grid.SetColumn(eye, 1);
                valueHost.Children.Add(eye);
            }
        }
        BuildValue();

        var panel = UI.Column(12, name, nameHint, valueHost, secret, expand, note);
        if (requested)
        {
            panel.Children.Insert(0, UI.WrapRow(UI.Icon(UI.Terminal, 16, UI.Accent),
                UI.Text($"A command in your terminal asked for {draft.Key}. Paste its value here: it goes to Windows Credential Manager, and the agent never sees it.", color: UI.Secondary)));
        }
        var dialog = UI.Dialog(root, isNew ? $"New Variable in {project.Name}" : "Edit Variable", panel, 520);
        dialog.PrimaryButtonText = isNew ? "Add" : "Save";
        if (isNew && !requested) dialog.SecondaryButtonText = "Add & New";
        dialog.CloseButtonText = "Cancel";

        void Validate()
        {
            var key = name.Text.Trim();
            var problem = key.Length == 0 ? null : EnvVar.KeyProblem(key);
            var clashes = project.Variables.Any(v => EnvVar.SameKey(v.Key, key) && v.Id != draft.Id);
            nameHint.Text = problem ?? (clashes ? (isNew ? $"{key} already exists here; adding replaces its value." : $"Another {key} exists here; the last one wins.") : "");
            nameHint.Foreground = problem is not null ? UI.Critical : UI.Caution;
            nameHint.Visibility = nameHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = key.Length > 0 && problem is null && !(requested && draft.Value.Length == 0);
        }
        validate = Validate;
        name.TextChanged += (_, _) =>
        {
            draft.Key = name.Text;
            if (!secretTouched && secret.IsOn != EnvVar.LooksSecret(name.Text.Trim())) { secret.IsOn = EnvVar.LooksSecret(name.Text.Trim()); secretTouched = false; }
            Validate();
        };
        secret.Toggled += (_, _) =>
        {
            if (secret.FocusState != FocusState.Unfocused) secretTouched = true;
            if (draft.IsSecret != secret.IsOn) { draft.IsSecret = secret.IsOn; BuildValue(); }
        };
        expand.Toggled += (_, _) => draft.Expands = expand.IsOn;
        note.TextChanged += (_, _) => draft.Note = note.Text;
        Validate();

        void Save()
        {
            var variable = draft with { Key = name.Text.Trim() };
            if (isNew) state.Store.AddVariables([variable], projectId, MergePolicy.Replace, "Add Variable");
            else state.Store.ReplaceVariable(variable, projectId);
        }
        dialog.SecondaryButtonClick += (_, args) =>
        {
            // Add & New: keep the dialog open for the next one.
            args.Cancel = true;
            Save();
            draft = new EnvVar("");
            secretTouched = false;
            reveal = false;
            name.Text = "";
            note.Text = "";
            secret.IsOn = false;
            expand.IsOn = false;
            BuildValue();
            name.Focus(FocusState.Programmatic);
        };
        dialog.Opened += (_, _) =>
        {
            if (template is not null && valueBox is not null) valueBox.Focus(FocusState.Programmatic);
            else name.Focus(FocusState.Programmatic);
        };
        if (await Show(dialog, wait: requested) == ContentDialogResult.Primary) Save();
    }

    // Import .env / appsettings.json

    public static async Task Import(AppState state, Guid projectId, string text)
    {
        if (state.XamlRoot is not { } root || state.Store.Config.Find(projectId) is not { } project) return;
        // AcceptsReturn before Text: a single-line TextBox keeps only the first line of what it's given.
        var input = new TextBox { AcceptsReturn = true, Text = text, FontFamily = UI.Mono, Height = 130, TextWrapping = TextWrapping.NoWrap, PlaceholderText = "API_URL=http://localhost:3000\nOPENAI_API_KEY=sk-…" };
        ScrollViewer.SetHorizontalScrollBarVisibility(input, ScrollBarVisibility.Auto);
        var replace = new RadioButtons { Header = "If a name already exists:", MaxColumns = 2, Items = { "Replace its value", "Keep the current value" }, SelectedIndex = 0 };
        var detectSecrets = new CheckBox { Content = "Treat names like *_TOKEN, *_SECRET, *_KEY and *PASSWORD* as secrets", IsChecked = true };
        var expandVariables = new CheckBox { Content = "Expand $VARIABLES in values that aren't single-quoted", IsChecked = false };
        var preview = new StackPanel { Spacing = 2 };
        var previewScroll = new ScrollViewer { Content = preview, Height = 180 };
        var openFile = UI.Button("Open File…", async () =>
        {
            if (state.Window is { } w && await UI.PickFile(w) is { } path)
            {
                try { input.Text = await File.ReadAllTextAsync(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        });
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(UI.Text("Paste the contents of a .env or appsettings.json file, or open one.", color: UI.Secondary));
        Grid.SetColumn(openFile, 1);
        top.Children.Add(openFile);

        var panel = UI.Column(12, top, input, previewScroll, replace, detectSecrets, expandVariables);
        var dialog = UI.Dialog(root, $"Import into {project.Name}", panel, 680);
        dialog.CloseButtonText = "Cancel";
        List<EnvVar> importable = [];

        void Recompute()
        {
            preview.Children.Clear();
            importable = [];
            var existing = project.Variables.Select(v => v.Key).ToHashSet(EnvVar.KeyComparer);
            var entries = DotEnv.ParseAny(input.Text);
            if (entries.Count == 0)
                preview.Children.Add(UI.Text(input.Text.Length == 0 ? "Nothing to import yet." : "No KEY=value lines or JSON settings found.", color: UI.Secondary));
            foreach (var entry in entries)
            {
                var v = new EnvVar(entry.Key, entry.Value,
                    isSecret: detectSecrets.IsChecked == true && EnvVar.LooksSecret(entry.Key),
                    expands: expandVariables.IsChecked == true && entry.Quote != DotEnv.Quote.Single && entry.Value.Contains('$'));
                string result;
                var ok = true;
                if (v.Problem is { } problem) { result = $"Skipped: {problem}"; ok = false; }
                else if (existing.Contains(v.Key))
                {
                    ok = replace.SelectedIndex == 0;
                    result = ok ? "Replaces current value" : "Skipped: already exists";
                }
                else result = "New";
                if (ok) importable.Add(v);
                preview.Children.Add(PreviewRow(v, result, ok));
            }
            dialog.PrimaryButtonText = importable.Count == 1 ? "Import 1 Variable" : $"Import {importable.Count} Variables";
            dialog.IsPrimaryButtonEnabled = importable.Count > 0;
        }
        input.TextChanged += (_, _) => Recompute();
        replace.SelectionChanged += (_, _) => Recompute();
        detectSecrets.Click += (_, _) => Recompute();
        expandVariables.Click += (_, _) => Recompute();
        Recompute();
        if (await Show(dialog) == ContentDialogResult.Primary)
            state.Store.AddVariables(importable, projectId, MergePolicy.Replace, "Import Variables");
    }

    static Grid PreviewRow(EnvVar v, string result, bool ok)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        foreach (var w in new[] { 2.0, 2.5, 0.6, 2.0 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        var key = UI.Text(v.Key, color: ok ? null : UI.Secondary, wrap: false, mono: true);
        var value = UI.Text(v.IsSecret ? "••••••" : v.Value.Replace("\n", " ⏎ "), color: UI.Secondary, wrap: false);
        var flags = UI.Row(4);
        if (v.IsSecret) flags.Children.Add(UI.Icon(UI.Lock, 12, UI.Secondary));
        if (v.Expands) flags.Children.Add(UI.Text("$", 12, UI.Secondary, bold: true));
        var outcome = UI.Text(result, color: ok ? null : UI.Secondary, wrap: false);
        UIElement[] cells = [key, value, flags, outcome];
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn((FrameworkElement)cells[i], i);
            grid.Children.Add(cells[i]);
        }
        return grid;
    }

    // Copy from another project

    public static async Task CopyFrom(AppState state, Guid targetId)
    {
        if (state.XamlRoot is not { } root || state.Store.Config.Find(targetId) is not { } target) return;
        var sources = state.Store.Config.Scopes.Where(p => p.Id != targetId && p.Variables.Count > 0).ToList();
        var dialog = UI.Dialog(root, $"Copy Variables into {target.Name}", new StackPanel(), 560);
        dialog.CloseButtonText = "Cancel";
        if (sources.Count == 0)
        {
            dialog.Content = UI.Text("No other project has variables yet.", color: UI.Secondary);
            await Show(dialog);
            return;
        }

        var picker = new ComboBox { Header = "From:", ItemsSource = sources.Select(p => $"{p.Name} ({p.Variables.Count})").ToList(), SelectedIndex = 0, MinWidth = 280 };
        var items = new StackPanel { Spacing = 2 };
        var replace = new RadioButtons { Header = "If a name already exists:", MaxColumns = 2, Items = { "Replace its value", "Keep the current value" }, SelectedIndex = 0 };
        var chosen = new HashSet<Guid>();
        var targetKeys = target.Variables.Select(v => v.Key).ToHashSet(EnvVar.KeyComparer);

        void Count()
        {
            dialog.PrimaryButtonText = chosen.Count == 1 ? "Copy 1 Variable" : $"Copy {chosen.Count} Variables";
            dialog.IsPrimaryButtonEnabled = chosen.Count > 0;
        }
        void Fill()
        {
            items.Children.Clear();
            chosen.Clear();
            var source = sources[Math.Max(0, picker.SelectedIndex)];
            foreach (var v in source.Variables)
            {
                chosen.Add(v.Id);
                var label = UI.Row(8, UI.Text(v.Key, wrap: false, mono: true));
                if (targetKeys.Contains(v.Key)) label.Children.Add(UI.Caption("exists", UI.Caution));
                label.Children.Add(UI.Text(v.IsSecret ? "••••••" : v.Value, color: UI.Secondary, wrap: false));
                var box = new CheckBox { Content = label, IsChecked = true };
                var id = v.Id;
                box.Click += (_, _) => { if (box.IsChecked == true) chosen.Add(id); else chosen.Remove(id); Count(); };
                items.Children.Add(box);
            }
            Count();
        }
        picker.SelectionChanged += (_, _) => Fill();
        var selectAll = UI.Link("Select All", () => { foreach (var b in items.Children.OfType<CheckBox>()) b.IsChecked = true; chosen.UnionWith(sources[picker.SelectedIndex].Variables.Select(v => v.Id)); Count(); });
        var selectNone = UI.Link("Select None", () => { foreach (var b in items.Children.OfType<CheckBox>()) b.IsChecked = false; chosen.Clear(); Count(); });
        dialog.Content = new ScrollViewer
        {
            Content = UI.Column(12, picker, new ScrollViewer { Content = items, Height = 240 }, UI.Row(16, selectAll, selectNone), replace),
        };
        Fill();
        if (await Show(dialog) == ContentDialogResult.Primary)
        {
            var source = sources[picker.SelectedIndex];
            var policy = replace.SelectedIndex == 0 ? MergePolicy.Replace : MergePolicy.KeepExisting;
            state.Store.AddVariables(source.Variables.Where(v => chosen.Contains(v.Id)), targetId, policy, "Copy Variables");
        }
    }

    // New project

    public static async Task NewProject(AppState state)
    {
        if (state.XamlRoot is not { } root) return;
        string? folder = null;
        FoundProject? found = null;
        var name = new TextBox { Header = "Name", PlaceholderText = "my-app" };
        var folderText = UI.Text("None: loads in every shell", color: UI.Secondary, wrap: false);
        var clear = UI.IconButton(UI.Close, "Don't link a folder", () => { }, 12);
        clear.Visibility = Visibility.Collapsed;
        var importFound = new CheckBox { IsChecked = true, Visibility = Visibility.Collapsed };
        var scopes = state.Store.Config.Scopes.Where(p => p.Variables.Count > 0).ToList();
        var startWith = new ComboBox { Header = "Start with", MinWidth = 280, SelectedIndex = 0 };
        startWith.Items.Add("No variables");
        foreach (var p in scopes) startWith.Items.Add($"A copy of {p.Name} ({p.Variables.Count})");
        startWith.SelectedIndex = 0;

        var choose = UI.Button("Choose…", async () =>
        {
            if (state.Window is not { } w || await UI.PickFolder(w) is not { } path) return;
            folder = path;
            folderText.Text = state.Store.Paths.Abbreviate(path);
            folderText.Foreground = UI.Brush("TextFillColorPrimaryBrush");
            clear.Visibility = Visibility.Visible;
            if (name.Text.Trim().Length == 0) name.Text = Path.GetFileName(PathStyle.TrimTrailing(path));
            found = await Task.Run(() => ProjectScanner.Project(path, state.Store.Paths.UserSecretsRoot));
            importFound.Content = found is null ? "" : $"Import its {UI.Plural(found.Variables.Count, "variable", "variables")} ({string.Join(" + ", found.Files)})";
            importFound.Visibility = found is null ? Visibility.Collapsed : Visibility.Visible;
        });
        clear.Click += (_, _) =>
        {
            folder = null;
            found = null;
            folderText.Text = "None: loads in every shell";
            folderText.Foreground = UI.Secondary;
            clear.Visibility = importFound.Visibility = Visibility.Collapsed;
        };

        var folderRow = UI.Column(4, UI.Caption("Folder"), UI.Row(8, folderText, clear, choose),
            UI.Caption("With a folder, the project's variables load only while a shell is inside it."));
        var dialog = UI.Dialog(root, "New Project", UI.Column(14, name, folderRow, importFound, startWith), 520);
        dialog.PrimaryButtonText = "Create Project";
        dialog.CloseButtonText = "Cancel";
        dialog.IsPrimaryButtonEnabled = false;
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0;
        dialog.Opened += (_, _) => name.Focus(FocusState.Programmatic);
        if (await Show(dialog) != ContentDialogResult.Primary) return;
        var copyFrom = startWith.SelectedIndex > 0 ? scopes[startWith.SelectedIndex - 1].Id : (Guid?)null;
        var extra = importFound.IsChecked == true && found is not null ? found.Variables : [];
        var project = state.Store.CreateProject(name.Text, folder, copyFrom, extra);
        state.Window?.Select(project.Id);
    }

    // Find projects in a folder

    public static async Task ScanProjects(AppState state)
    {
        if (state.XamlRoot is not { } root) return;
        var intro = UI.Text("Envy looks for .env, .env.local and .envrc files, and for .NET appsettings.json and user secrets. Each folder it finds becomes a project that loads when a shell enters it.", color: UI.Secondary);
        var dialog = UI.Dialog(root, "Import Projects", UI.Column(12, intro, new ProjectScanView(state)), 620);
        dialog.CloseButtonText = "Done";
        dialog.DefaultButton = ContentDialogButton.Close;
        await Show(dialog);
    }

    // Export

    public static async Task Export(AppState state, Guid projectId)
    {
        if (state.Window is not { } window || state.Store.Config.Find(projectId) is not { Variables.Count: > 0 } p) return;
        if (await UI.PickSaveFile(window, ".env") is not { } path) return;
        try
        {
            state.Store.ExportDotEnv(p.Id, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (state.XamlRoot is { } root) await UI.Alert(root, "Couldn't export", e.Message);
        }
    }
}

/// <summary>Picks a folder of code, finds projects with environment files in it, and imports the chosen ones.</summary>
public sealed class ProjectScanView : StackPanel
{
    readonly AppState state;
    readonly Action<int>? onImported;
    readonly StackPanel results = new() { Spacing = 8 };
    readonly StackPanel buttons;

    public ProjectScanView(AppState state, Action<int>? onImported = null)
    {
        this.state = state;
        this.onImported = onImported;
        Spacing = 12;
        buttons = UI.Row(8);
        foreach (var suggestion in state.Store.Paths.SuggestedProjectRoots.Take(3))
            buttons.Children.Add(UI.Button($"Scan {state.Store.Paths.Abbreviate(suggestion)}", () => Scan(suggestion)));
        buttons.Children.Add(UI.Button("Choose Folder…", async () =>
        {
            if (state.Window is { } w && await UI.PickFolder(w) is { } path) Scan(path);
        }));
        Children.Add(buttons);
        Children.Add(results);
    }

    async void Scan(string folder)
    {
        buttons.IsHitTestVisible = false;
        buttons.Opacity = 0.5;
        results.Children.Clear();
        results.Children.Add(UI.Row(8, new ProgressRing { IsActive = true, Width = 16, Height = 16 },
            UI.Text($"Looking for environment files in {state.Store.Paths.Abbreviate(folder)}…", color: UI.Secondary)));
        var appData = state.Store.Paths.UserSecretsRoot;
        var found = await Task.Run(() => ProjectScanner.Scan(folder, appData));
        buttons.IsHitTestVisible = true;
        buttons.Opacity = 1;
        results.Children.Clear();
        if (found.Count == 0)
        {
            results.Children.Add(UI.Text($"No folders with .env files or .NET settings in {state.Store.Paths.Abbreviate(folder)}.", color: UI.Secondary));
            return;
        }

        var chosen = found.Where(f => !state.Store.IsLinked(f.Folder)).Select(f => f.Id).ToHashSet();
        var list = new StackPanel { Spacing = 4 };
        var import = UI.Button("", () => { }, accent: true);
        void Count()
        {
            import.Content = chosen.Count == 1 ? "Import 1 Project" : $"Import {chosen.Count} Projects";
            import.IsEnabled = chosen.Count > 0;
        }
        foreach (var project in found)
        {
            var linked = state.Store.IsLinked(project.Folder);
            var secrets = project.Variables.Count(v => v.IsSecret);
            var summary = UI.Plural(project.Variables.Count, "variable", "variables") + (secrets > 0 ? $", {secrets} secret" : "");
            var title = UI.Row(6, UI.Text(project.Name, bold: true, wrap: false), UI.Caption(string.Join(" + ", project.Files)));
            if (linked) title.Children.Add(UI.Caption("already added", UI.Caution));
            var label = UI.Column(0, title, UI.Caption($"{state.Store.Paths.Abbreviate(project.Folder)} · {summary}"));
            var box = new CheckBox { Content = label, IsChecked = chosen.Contains(project.Id), IsEnabled = !linked };
            var id = project.Id;
            box.Click += (_, _) => { if (box.IsChecked == true) chosen.Add(id); else chosen.Remove(id); Count(); };
            list.Children.Add(box);
        }
        import.Click += (_, _) =>
        {
            var count = state.Store.ImportProjects(found.Where(f => chosen.Contains(f.Id)));
            results.Children.Clear();
            results.Children.Add(UI.Row(8, UI.Icon(UI.Verified, 16, UI.Success), UI.Text(count == 1 ? "Created 1 project." : $"Created {count} projects.", bold: true)));
            results.Children.Add(UI.Text("Each loads automatically while a shell is inside its folder. Find them in the sidebar.", color: UI.Secondary));
            onImported?.Invoke(count);
        };
        Count();
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(UI.Text($"Found {found.Count} in {state.Store.Paths.Abbreviate(folder)}", color: UI.Secondary));
        Grid.SetColumn(import, 1);
        footer.Children.Add(import);
        results.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260 });
        results.Children.Add(footer);
    }
}

using Envy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace Envy.App;

/// <summary>Global or one project: its header (name, on/off, folders), a toolbar, and the table of variables.</summary>
public sealed class ProjectPage : Grid
{
    readonly AppState state;
    readonly Guid projectId;
    readonly MainWindow window;
    readonly Grid header = new() { Padding = new Thickness(24, 16, 24, 8) };
    readonly TextBox search = new() { PlaceholderText = "Filter variables", Width = 220 };
    readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Extended, Padding = new Thickness(12, 0, 12, 12) };
    readonly Grid tableArea = new();
    readonly Border dropHighlight = new() { BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(10), Margin = new Thickness(6), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly Grid table = new();
    readonly List<VariableRow> rows = [];
    readonly HashSet<Guid> revealed = [];
    TextBox? nameBox;
    bool updating;

    public ProjectPage(AppState state, Guid projectId, MainWindow window)
    {
        this.state = state;
        this.projectId = projectId;
        this.window = window;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Children.Add(header);
        var toolbar = Toolbar();
        SetRow(toolbar, 1);
        Children.Add(toolbar);
        SetRow(tableArea, 2);
        Children.Add(tableArea);
        dropHighlight.BorderBrush = UI.Accent;
        SetRowSpan(dropHighlight, 3);
        Children.Add(dropHighlight);

        list.ItemContainerStyle = new Style(typeof(ListViewItem))
        {
            Setters =
            {
                new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                new Setter(FrameworkElement.MinHeightProperty, 44.0),
                new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0)),
            },
        };
        list.KeyDown += OnListKeyDown;
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        table.Children.Add(VariableRow.HeaderRow());
        SetRow(list, 1);
        table.Children.Add(list);
        search.TextChanged += (_, _) => SyncRows();

        AllowDrop = true;
        DragOver += OnDragOver;
        DragLeave += (_, _) => dropHighlight.Visibility = Visibility.Collapsed;
        Drop += OnDrop;

        state.Changed += OnChanged;
        RefreshHeader();
        SyncRows();
    }

    public void Detach() => state.Changed -= OnChanged;

    Project? Project => state.Store.Config.Find(projectId);

    void OnChanged(bool structural)
    {
        if (Project is null) return;
        if (structural)
        {
            RefreshHeader();
            SyncRows();
        }
        else
        {
            // Something was typed into a field here; rebuilding would take the focus away from it.
            foreach (var row in rows) row.RefreshWarnings();
        }
    }

    public void FocusSearch() => search.Focus(FocusState.Keyboard);

    // Header

    void RefreshHeader()
    {
        if (Project is not { } p) return;
        updating = true;
        // The name box is kept across rebuilds, so typing in it keeps the focus. Empty the old details panel first: the
        // box would otherwise still be its child, and adding it to the new one throws.
        foreach (var panel in header.Children.OfType<Panel>()) panel.Children.Clear();
        header.Children.Clear();
        header.ColumnDefinitions.Clear();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = p.IsGlobal ? UI.Globe : p.IsFolderScoped ? UI.Folder : UI.Package;
        var icon = UI.Icon(glyph, 24, p.IsEnabled ? UI.Accent : UI.Secondary);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 6, 0, 0);
        header.Children.Add(icon);

        var details = new StackPanel { Spacing = 6 };
        if (p.IsGlobal)
        {
            details.Children.Add(UI.Text("Global", 24, bold: true));
        }
        else
        {
            if (nameBox is null)
            {
                nameBox = new TextBox { FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), PlaceholderText = "Project name" };
                nameBox.TextChanged += (_, _) => { if (!updating) state.Store.RenameProject(projectId, nameBox.Text); };
            }
            if (nameBox.Text != p.Name) nameBox.Text = p.Name;
            details.Children.Add(nameBox);
        }
        details.Children.Add(UI.Text(ScopeText(p), color: UI.Secondary));
        if (!p.IsGlobal) details.Children.Add(Folders(p));
        SetColumn(details, 1);
        header.Children.Add(details);

        var onOff = new ToggleSwitch { IsOn = p.IsEnabled, OnContent = "On", OffContent = "Off", VerticalAlignment = VerticalAlignment.Top };
        onOff.Toggled += (_, _) => { if (!updating) state.Store.SetProjectEnabled(projectId, onOff.IsOn); };
        SetColumn(onOff, 2);
        header.Children.Add(onOff);
        updating = false;
    }

    static string ScopeText(Project p)
    {
        if (!p.IsEnabled) return "Off: none of these variables are loaded.";
        if (p.IsGlobal) return "Loaded in every shell and the coding agents you start there. Projects can override these.";
        if (p.IsFolderScoped) return "Loaded only while a shell is inside one of these folders (or a subfolder):";
        return "Loaded in every shell and the coding agents you start there.";
    }

    StackPanel Folders(Project p)
    {
        var panel = new StackPanel { Spacing = 2 };
        foreach (var folder in p.Folders)
        {
            var path = new TextBlock { Text = state.Store.Paths.Abbreviate(folder), FontFamily = UI.Mono, FontSize = 13, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            var row = UI.Row(6, UI.Icon(UI.Folder, 14, UI.Secondary), path);
            if (!Directory.Exists(folder)) row.Children.Add(UI.Caption("missing", UI.Caution));
            var f = folder;
            row.Children.Add(UI.IconButton(UI.OpenFolder, "Show in File Explorer", () => UI.ShowInExplorer(f), 12));
            row.Children.Add(UI.IconButton(UI.Close, "Unlink this folder", () => state.Store.UnlinkFolder(f, projectId), 12));
            panel.Children.Add(row);
        }
        panel.Children.Add(UI.Link(p.IsFolderScoped ? "Link Another Folder…" : "Load Only Inside a Folder…", async () =>
        {
            if (await UI.PickFolder(window) is { } folder) state.Store.LinkFolder(folder, projectId);
        }));
        return panel;
    }

    // Toolbar

    Grid Toolbar()
    {
        var bar = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsDynamicOverflowEnabled = false, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        var add = new AppBarButton { Label = "Add Variable", Icon = new FontIcon { Glyph = UI.Add } };
        add.Click += (_, _) => _ = Dialogs.EditVariable(state, projectId, null);
        ToolTipService.SetToolTip(add, "Add a variable (Ctrl+N)");
        bar.PrimaryCommands.Add(add);

        var import = new AppBarButton { Label = "Import", Icon = new FontIcon { Glyph = UI.Import } };
        var importMenu = new MenuFlyout();
        AddItem(importMenu, "Paste or Open .env File…", () => _ = Dialogs.Import(state, projectId, ""));
        AddItem(importMenu, "Copy from Another Project…", () => _ = Dialogs.CopyFrom(state, projectId));
        import.Flyout = importMenu;
        bar.PrimaryCommands.Add(import);

        var export = new AppBarButton { Label = "Export .env", Icon = new FontIcon { Glyph = UI.Export } };
        export.Click += (_, _) => _ = Dialogs.Export(state, projectId);
        ToolTipService.SetToolTip(export, "Export the enabled variables as a .env file (Ctrl+Shift+E)");
        bar.PrimaryCommands.Add(export);

        if (Project is { IsGlobal: false })
        {
            var more = new AppBarButton { Label = "More", Icon = new FontIcon { Glyph = UI.More } };
            var moreMenu = new MenuFlyout();
            AddItem(moreMenu, "Duplicate Project", () => { if (state.Store.DuplicateProject(projectId) is { } copy) window.Select(copy.Id); });
            moreMenu.Items.Add(new MenuFlyoutSeparator());
            AddItem(moreMenu, "Delete Project…", () => _ = window.DeleteProject(projectId));
            more.Flyout = moreMenu;
            bar.PrimaryCommands.Add(more);
        }

        var grid = new Grid { Padding = new Thickness(16, 0, 24, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(bar);
        search.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(search, 1);
        grid.Children.Add(search);
        return grid;
    }

    static void AddItem(MenuFlyout menu, string text, Action action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    // Table

    void SyncRows()
    {
        if (Project is not { } p) return;
        tableArea.Children.Clear();
        if (p.Variables.Count == 0)
        {
            tableArea.Children.Add(EmptyState(p));
            return;
        }

        var query = search.Text.Trim();
        var visible = p.Variables.Where(v => query.Length == 0
            || v.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
            || v.Note.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (!v.IsSecret && v.Value.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();

        // Rows are rebuilt (they're cheap); what's revealed and selected carries over.
        var selected = list.SelectedItems.OfType<VariableRow>().Select(r => r.VariableId).ToHashSet();
        list.Items.Clear();
        rows.Clear();
        foreach (var v in visible)
        {
            var row = new VariableRow(state, projectId, v.Id, revealed, this);
            rows.Add(row);
            list.Items.Add(row);
            if (selected.Contains(v.Id)) list.SelectedItems.Add(row);
        }
        tableArea.Children.Add(table);
    }

    UIElement EmptyState(Project p)
    {
        var buttons = UI.Row(8,
            UI.Button("Add Variable", () => _ = Dialogs.EditVariable(state, projectId, null), accent: true),
            UI.Button("Paste or Open .env…", () => _ = Dialogs.Import(state, projectId, "")));
        if (state.Store.Config.Scopes.Any(s => s.Id != p.Id && s.Variables.Count > 0))
            buttons.Children.Add(UI.Button("Copy from Project…", () => _ = Dialogs.CopyFrom(state, projectId)));
        buttons.HorizontalAlignment = HorizontalAlignment.Center;
        var panel = UI.Column(12,
            UI.Icon(UI.Key, 40, UI.Secondary),
            UI.Text("No Variables Yet", 20, bold: true),
            UI.Text("Add variables one at a time, paste or drop a .env or appsettings.json file here, or copy them from another project.", color: UI.Secondary),
            buttons);
        foreach (var child in panel.Children.OfType<FrameworkElement>()) child.HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)panel.Children[2]).TextAlignment = TextAlignment.Center;
        panel.MaxWidth = 460;
        panel.VerticalAlignment = VerticalAlignment.Center;
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        return panel;
    }

    /// <summary>The selected variables, or just <paramref name="clicked"/> when it isn't part of the selection.</summary>
    public HashSet<Guid> Targets(VariableRow clicked)
    {
        var selected = list.SelectedItems.OfType<VariableRow>().ToList();
        if (!selected.Contains(clicked))
        {
            list.SelectedItems.Clear();
            list.SelectedItems.Add(clicked);
            return [clicked.VariableId];
        }
        return selected.Select(r => r.VariableId).ToHashSet();
    }

    public MenuFlyout ContextMenu(VariableRow clicked)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (Project is not { } p) return;
            var ids = Targets(clicked);
            var vars = p.Variables.Where(v => ids.Contains(v.Id)).ToList();
            if (vars.Count == 0) return;
            if (vars.Count == 1) AddItem(menu, "Edit…", () => _ = Dialogs.EditVariable(state, projectId, vars[0].Id));
            var allOn = vars.All(v => v.IsEnabled);
            AddItem(menu, allOn ? "Turn Off" : "Turn On", () => state.Store.UpdateVariables(ids, projectId, allOn ? "Turn Off" : "Turn On", v => v.IsEnabled = !allOn));
            var allSecret = vars.All(v => v.IsSecret);
            AddItem(menu, allSecret ? "Make Plain" : "Make Secret", () => state.Store.UpdateVariables(ids, projectId, allSecret ? "Make Plain" : "Make Secret", v => v.IsSecret = !allSecret));
            menu.Items.Add(new MenuFlyoutSeparator());
            var targets = state.Store.Config.Scopes.Where(s => s.Id != projectId).ToList();
            var copyTo = new MenuFlyoutSubItem { Text = "Copy to", IsEnabled = targets.Count > 0 };
            var moveTo = new MenuFlyoutSubItem { Text = "Move to", IsEnabled = targets.Count > 0 };
            foreach (var target in targets)
            {
                var copy = new MenuFlyoutItem { Text = target.Name };
                copy.Click += (_, _) => state.Store.CopyVariables(ids, projectId, target.Id, move: false);
                copyTo.Items.Add(copy);
                var move = new MenuFlyoutItem { Text = target.Name };
                move.Click += (_, _) => state.Store.CopyVariables(ids, projectId, target.Id, move: true);
                moveTo.Items.Add(move);
            }
            menu.Items.Add(copyTo);
            menu.Items.Add(moveTo);
            AddItem(menu, "Copy as .env", () => UI.CopyText(state.Store.DotEnvText(ids, projectId)));
            menu.Items.Add(new MenuFlyoutSeparator());
            AddItem(menu, ids.Count == 1 ? "Delete" : $"Delete {ids.Count} Variables", () => state.Store.DeleteVariables(ids, projectId));
        };
        return menu;
    }

    async void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Typing in a field keeps its own Delete, copy and paste.
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var selected = list.SelectedItems.OfType<VariableRow>().Select(r => r.VariableId).ToHashSet();
        if (e.Key == VirtualKey.Delete && selected.Count > 0)
        {
            e.Handled = true;
            state.Store.DeleteVariables(selected, projectId);
        }
        else if (ctrl && e.Key == VirtualKey.C && selected.Count > 0)
        {
            e.Handled = true;
            UI.CopyText(state.Store.DotEnvText(selected, projectId));
        }
        else if (ctrl && e.Key == VirtualKey.V)
        {
            e.Handled = true;
            if (await UI.ClipboardText() is { } text && DotEnv.ParseAny(text).Count > 0) _ = Dialogs.Import(state, projectId, text);
        }
    }

    // Drop .env or appsettings.json files onto the page to import them.

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Import variables";
        dropHighlight.Visibility = Visibility.Visible;
    }

    async void OnDrop(object sender, DragEventArgs e)
    {
        dropHighlight.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var texts = new List<string>();
        foreach (var item in await e.DataView.GetStorageItemsAsync())
        {
            if (item is not StorageFile file) continue;
            try { texts.Add(await File.ReadAllTextAsync(file.Path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var text = string.Join("\n", texts);
        if (DotEnv.ParseAny(text).Count > 0) _ = Dialogs.Import(state, projectId, text);
    }
}

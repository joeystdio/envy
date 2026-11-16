using Envy.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Envy.App;

/// <summary>
/// The main window: a NavigationView with Global, the projects and Shell Integration, a banner for problems that stop
/// variables reaching your shells, and the page for the selection. Closing it keeps Envy in the notification area.
/// </summary>
public sealed class MainWindow : Window
{
    readonly AppState state;
    readonly NavigationView nav = new();
    readonly InfoBar banner = new() { IsClosable = false };
    readonly Grid content = new();
    readonly Grid overlay = new() { Visibility = Visibility.Collapsed };
    bool reallyClosing;
    Guid? selectedProject = Project.GlobalId;
    bool shellSelected;
    bool rebuildingNav;
    ProjectPage? projectPage;

    public MainWindow(AppState state)
    {
        this.state = state;
        Title = state.Store.Paths.IsSandbox ? $"Envy (sandbox: {state.Store.Paths.Home})" : "Envy";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Envy.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 760));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 560;
        }

        // Title bar drawn by the app, so Mica runs all the way to the top.
        ExtendsContentIntoTitleBar = true;
        var titleBar = new Grid { Height = 40, Padding = new Thickness(16, 0, 0, 0) };
        titleBar.Children.Add(UI.Row(10,
            new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Envy.ico"))), Width = 16, Height = 16 },
            UI.Text(Title, 12, wrap: false)));

        nav.IsSettingsVisible = false;
        nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        nav.OpenPaneLength = 300;
        nav.IsPaneToggleButtonVisible = false;
        nav.SelectionChanged += OnNavSelection;
        nav.PaneFooter = PaneFooter();

        var page = new Grid();
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        banner.Margin = new Thickness(16, 8, 16, 0);
        page.Children.Add(banner);
        Grid.SetRow(content, 1);
        page.Children.Add(content);
        nav.Content = page;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(titleBar);
        Grid.SetRow(nav, 1);
        root.Children.Add(nav);
        Grid.SetRowSpan(overlay, 2);
        overlay.Background = UI.Brush("SolidBackgroundFillColorBaseBrush");
        root.Children.Add(overlay);
        AddShortcuts(root);
        Content = root;
        SetTitleBar(titleBar);

        state.Changed += structural =>
        {
            RebuildNav();
            if (structural && selectedProject is { } id && state.Store.Config.Find(id) is null) Select(Project.GlobalId);
        };
        state.StatusChanged += UpdateBanner;
        AppWindow.Closing += (_, e) =>
        {
            if (reallyClosing) return;
            // Like closing the window on a Mac: Envy stays in the notification area.
            e.Cancel = true;
            state.SaveNow();
            AppWindow.Hide();
        };

        RebuildNav();
        Select(Project.GlobalId);
        UpdateBanner();
        root.Loaded += (_, _) =>
        {
            if (!state.Store.AnyConnected && !state.Store.HasOnboarded) ShowSetupAssistant();
        };
    }

    public void ShowAndActivate()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
    }

    public void CloseForReal()
    {
        reallyClosing = true;
        Close();
    }

    // Navigation

    UIElement PaneFooter()
    {
        var newProject = UI.Button("New Project", () => _ = Dialogs.NewProject(state), glyph: UI.Add);
        UI.SetToolTip(newProject, "New project (Ctrl+Shift+N)");
        var scan = UI.IconButton(UI.Import, "Import projects from a folder (Ctrl+Shift+I)", () => _ = Dialogs.ScanProjects(state));
        var grid = new Grid { Margin = new Thickness(12, 8, 12, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(newProject);
        Grid.SetColumn(scan, 1);
        grid.Children.Add(scan);
        return grid;
    }

    void RebuildNav()
    {
        rebuildingNav = true;
        var config = state.Store.Config;
        nav.MenuItems.Clear();
        nav.MenuItems.Add(ProjectItem(config.Global));
        nav.MenuItems.Add(new NavigationViewItemHeader { Content = "Projects" });
        foreach (var p in config.Projects) nav.MenuItems.Add(ProjectItem(p));
        if (config.Projects.Count == 0)
        {
            nav.MenuItems.Add(new NavigationViewItemHeader
            {
                Content = UI.Column(4, UI.Caption("No projects yet"), UI.Link("Find Projects in a Folder…", () => _ = Dialogs.ScanProjects(state))),
            });
        }
        nav.FooterMenuItems.Clear();
        var shell = new NavigationViewItem
        {
            Content = "Shell Integration",
            Icon = new FontIcon { Glyph = UI.Terminal, Foreground = state.Store.AnyConnected ? UI.Success : UI.Caution },
            Tag = "shell",
        };
        nav.FooterMenuItems.Add(shell);
        nav.SelectedItem = shellSelected ? shell
            : nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag is Guid g && g == selectedProject);
        rebuildingNav = false;
    }

    NavigationViewItem ProjectItem(Project p)
    {
        var toggle = new ToggleSwitch { IsOn = p.IsEnabled, OnContent = null, OffContent = null, MinWidth = 0, Margin = new Thickness(0, 0, -76, 0) };
        UI.SetToolTip(toggle, p.IsEnabled ? "Loaded. Click to turn off." : "Off. Click to load these variables.");
        var id = p.Id;
        toggle.Toggled += (_, _) => { if (!rebuildingNav) state.Store.SetProjectEnabled(id, toggle.IsOn); };

        var labels = UI.Column(0, UI.Text(p.Name, wrap: false), UI.Text(state.Store.Subtitle(p), 12, UI.Secondary, wrap: false));
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(labels);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);

        var glyph = p.IsGlobal ? UI.Globe : p.IsFolderScoped ? UI.Folder : UI.Package;
        var item = new NavigationViewItem
        {
            Content = row,
            Icon = new FontIcon { Glyph = glyph, Foreground = p.IsEnabled ? UI.Accent : UI.Secondary },
            Tag = id,
            Height = 52,
        };
        if (!p.IsGlobal) item.ContextFlyout = ProjectMenu(p);
        return item;
    }

    MenuFlyout ProjectMenu(Project p)
    {
        var menu = new MenuFlyout();
        void Add(string text, Action action) { var i = new MenuFlyoutItem { Text = text }; i.Click += (_, _) => action(); menu.Items.Add(i); }
        var id = p.Id;
        Add(p.IsEnabled ? "Turn Off" : "Turn On", () => state.Store.SetProjectEnabled(id, !p.IsEnabled));
        Add("Duplicate", () => { if (state.Store.DuplicateProject(id) is { } copy) Select(copy.Id); });
        menu.Items.Add(new MenuFlyoutSeparator());
        var index = state.Store.Config.Projects.FindIndex(x => x.Id == id);
        if (index > 0) Add("Move Up", () => state.Store.MoveProject(id, index - 1));
        if (index < state.Store.Config.Projects.Count - 1) Add("Move Down", () => state.Store.MoveProject(id, index + 1));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Delete…", () => _ = DeleteProject(id));
        return menu;
    }

    public async Task DeleteProject(Guid id)
    {
        if (state.Store.Config.Find(id) is not { } p || state.XamlRoot is not { } root) return;
        if (await UI.Confirm(root, $"Delete “{p.Name}”?", $"Its {UI.Plural(p.Variables.Count, "variable", "variables")} stop loading in your shells. You can undo this with Ctrl+Z.", "Delete Project"))
            state.Store.DeleteProject(id);
    }

    void OnNavSelection(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (rebuildingNav) return;
        switch ((args.SelectedItem as NavigationViewItem)?.Tag)
        {
            case Guid id: Select(id); break;
            case "shell": SelectShell(); break;
        }
    }

    public void Select(Guid id)
    {
        selectedProject = id;
        shellSelected = false;
        projectPage?.Detach();
        projectPage = new ProjectPage(state, id, this);
        content.Children.Clear();
        content.Children.Add(projectPage);
        RebuildNav();
        UpdateBanner();
    }

    public void SelectShell()
    {
        shellSelected = true;
        selectedProject = null;
        projectPage?.Detach();
        projectPage = null;
        content.Children.Clear();
        content.Children.Add(new ShellPage(state, this));
        RebuildNav();
        UpdateBanner();
    }

    public Guid? SelectedProjectId => shellSelected ? null : selectedProject;

    // Banner

    void UpdateBanner()
    {
        var store = state.Store;
        banner.ActionButton = null;
        if (store.Problem is { } problem)
        {
            banner.Severity = InfoBarSeverity.Error;
            banner.Title = "Your shells weren't updated";
            banner.Message = problem;
            if (store.SecretsLocked) banner.ActionButton = UI.Button("Try Again", store.RetrySecrets);
            banner.IsOpen = true;
        }
        else if (!store.AnyConnected && !shellSelected)
        {
            banner.Severity = InfoBarSeverity.Warning;
            banner.Title = "No shell is connected yet";
            banner.Message = "These variables don't load anywhere until you connect PowerShell, Git Bash or Command Prompt.";
            banner.ActionButton = UI.Button("Set Up…", ShowSetupAssistant);
            banner.IsOpen = true;
        }
        else
        {
            banner.IsOpen = false;
        }
        if (nav.FooterMenuItems.FirstOrDefault() is NavigationViewItem { Icon: FontIcon icon })
            icon.Foreground = store.AnyConnected ? UI.Success : UI.Caution;
    }

    // Setup Assistant, shown over the whole window

    public void ShowSetupAssistant()
    {
        overlay.Children.Clear();
        overlay.Children.Add(new SetupAssistant(state, this, CloseSetupAssistant));
        overlay.Visibility = Visibility.Visible;
    }

    void CloseSetupAssistant()
    {
        overlay.Visibility = Visibility.Collapsed;
        overlay.Children.Clear();
        RebuildNav();
        UpdateBanner();
    }

    // Keyboard shortcuts, as on the Mac with Ctrl for ⌘

    void AddShortcuts(UIElement root)
    {
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, e) =>
            {
                if (overlay.Visibility == Visibility.Visible) return;
                e.Handled = true;
                action();
            };
            root.KeyboardAccelerators.Add(accelerator);
        }
        var ctrl = VirtualKeyModifiers.Control;
        var ctrlShift = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift;
        Add(VirtualKey.N, ctrl, () => { if (SelectedProjectId is { } id) _ = Dialogs.EditVariable(state, id, null); });
        Add(VirtualKey.N, ctrlShift, () => _ = Dialogs.NewProject(state));
        Add(VirtualKey.I, ctrl, () => { if (SelectedProjectId is { } id) _ = Dialogs.Import(state, id, ""); });
        Add(VirtualKey.I, ctrlShift, () => _ = Dialogs.ScanProjects(state));
        Add(VirtualKey.E, ctrlShift, () => { if (SelectedProjectId is { } id) _ = Dialogs.Export(state, id); });
        Add(VirtualKey.Z, ctrl, state.Store.Undo);
        Add(VirtualKey.Y, ctrl, state.Store.Redo);
        Add(VirtualKey.Z, ctrlShift, state.Store.Redo);
        Add(VirtualKey.F, ctrl, () => projectPage?.FocusSearch());
    }
}

using Envy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace Envy.App;

/// <summary>
/// The application, without XAML files: Fluent control styles are added in code, and type lookups go to WinUI's own
/// metadata provider. Envy keeps running in the notification area when its window is closed, like the menu bar
/// extra on the Mac.
/// </summary>
public sealed class App : Application, IXamlMetadataProvider
{
    readonly XamlControlsXamlMetaDataProvider metadata = new();
    readonly bool startHidden;
    AppState? state;
    MainWindow? window;
    TrayIcon? tray;

    public App(bool startHidden)
    {
        this.startHidden = startHidden;
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Log(e.Exception);
        };
    }

    public static new App Current => (App)Application.Current;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        state = new AppState(new EnvyStore(new AppPaths(), Environment.ProcessPath));
        window = new MainWindow(state);
        state.Window = window;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Envy.ico");
        tray = new TrayIcon(iconPath, state.Store.Paths.IsSandbox ? "Envy (sandbox)" : "Envy");
        tray.Clicked += ShowMainWindow;
        tray.Menu = TrayMenu;

        if (!startHidden) ShowMainWindow();
        state.Start();
    }

    IReadOnlyList<TrayMenuItem> TrayMenu()
    {
        var s = state!;
        var items = new List<TrayMenuItem>
        {
            new("Global", () => s.Store.SetProjectEnabled(Project.GlobalId, !s.Store.Config.Global.IsEnabled), s.Store.Config.Global.IsEnabled),
        };
        if (s.Store.Config.Projects.Count > 0)
        {
            items.Add(TrayMenuItem.Separator);
            items.Add(new("Projects", Enabled: false));
            foreach (var p in s.Store.Config.Projects)
            {
                var title = p.Folders.Count > 0 ? $"{p.Name}  ·  {Path.GetFileName(PathStyle.TrimTrailing(p.Folders[0]))}\\" : p.Name;
                var id = p.Id;
                items.Add(new(title, () => s.Store.SetProjectEnabled(id, !(s.Store.Config.Find(id)?.IsEnabled ?? false)), p.IsEnabled));
            }
        }
        items.Add(TrayMenuItem.Separator);
        if (!s.Store.AnyConnected) items.Add(new("Set Up Envy…", () => { ShowMainWindow(); window!.ShowSetupAssistant(); }));
        items.Add(new("Open Envy", ShowMainWindow));
        items.Add(TrayMenuItem.Separator);
        items.Add(new("Quit Envy", Quit));
        return items;
    }

    public void ShowMainWindow() => window?.ShowAndActivate();

    public void Quit()
    {
        state?.SaveNow();
        tray?.Dispose();
        tray = null;
        window?.CloseForReal();
        Exit();
    }

    public static void Log(Exception e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Envy");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "errors.log"), $"{DateTime.Now:u} {e}\n\n");
        }
        catch (IOException) { }
    }

    public IXamlType GetXamlType(Type type) => metadata.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => metadata.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => metadata.GetXmlnsDefinitions();
}

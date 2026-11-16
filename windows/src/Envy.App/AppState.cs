using Envy.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Envy.App;

/// <summary>
/// What the window, its pages and the tray share: the store, a save shortly after every change (settings, secrets and
/// the shell files, which open shells pick up at their next prompt), and checks of fresh shells in the background.
/// </summary>
public sealed class AppState
{
    public EnvyStore Store { get; }
    public MainWindow? Window { get; set; }
    public XamlRoot? XamlRoot => Window?.Content?.XamlRoot;

    /// <summary>The config changed; true when projects or variables were added, removed or reordered.</summary>
    public event Action<bool>? Changed;
    /// <summary>Saving, problems, shell connections or shell checks changed.</summary>
    public event Action? StatusChanged;

    readonly DispatcherQueue dispatcher;
    readonly DispatcherQueueTimer saveTimer;
    public HashSet<ShellKind> Checking { get; } = [];

    public AppState(EnvyStore store)
    {
        Store = store;
        dispatcher = DispatcherQueue.GetForCurrentThread();
        saveTimer = dispatcher.CreateTimer();
        saveTimer.Interval = TimeSpan.FromMilliseconds(300);
        saveTimer.IsRepeating = false;
        saveTimer.Tick += (_, _) => Store.Save();
        Store.Changed += structural =>
        {
            saveTimer.Stop();
            saveTimer.Start();
            Changed?.Invoke(structural);
        };
        Store.StatusChanged += () => OnUi(() => StatusChanged?.Invoke());
    }

    /// <summary>Finds the shells and checks each connected one in the background.</summary>
    public void Start()
    {
        _ = Task.Run(() =>
        {
            Store.RefreshShells();
            OnUi(() => InspectAll());
        });
    }

    public void SaveNow()
    {
        saveTimer.Stop();
        Store.Save();
    }

    public void InspectAll()
    {
        foreach (var shell in Store.Shells) Inspect(shell.Kind);
    }

    /// <summary>Opens a fresh shell of this kind in the background to see whether it loads Envy.</summary>
    public void Inspect(ShellKind kind)
    {
        if (Store.Shells.FirstOrDefault(s => s.Kind == kind) is not { } shell || !Checking.Add(kind)) return;
        StatusChanged?.Invoke();
        _ = Task.Run(() =>
        {
            try { Store.Inspect(shell); }
            catch (Exception e) { App.Log(e); }
            OnUi(() =>
            {
                Checking.Remove(kind);
                StatusChanged?.Invoke();
            });
        });
    }

    /// <summary>All coding agents any checked shell found on its PATH.</summary>
    public List<string> Agents =>
        Store.Inspections.Values.Where(i => i is not null).SelectMany(i => i!.Agents).Distinct().OrderBy(a => a).ToList();

    public void OnUi(Action action)
    {
        if (dispatcher.HasThreadAccess) action();
        else dispatcher.TryEnqueue(() => action());
    }
}

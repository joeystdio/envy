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

    /// <summary>
    /// Listens for the envy command, brings instructions an older Envy wrote for coding agents up to date, then finds the
    /// shells and checks each connected one in the background.
    /// </summary>
    public void Start()
    {
        StartCommandServer();
        Store.UpdateOutdatedInstructions();
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

    // The envy command

    CommandPipeServer? commandServer;

    /// <summary>Why the envy command can't reach this app, when it can't.</summary>
    public string? CommandProblem { get; private set; }

    void StartCommandServer()
    {
        try
        {
            var server = new CommandPipeServer(CommandPipe.Name(Store.Paths), request =>
            {
                var done = new TaskCompletionSource<CommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                var queued = dispatcher.TryEnqueue(() =>
                {
                    try { done.SetResult(HandleCommand(request)); }
                    catch (Exception e)
                    {
                        App.Log(e);
                        done.SetResult(new CommandResponse { Error = e.Message, ExitCode = 1 });
                    }
                });
                if (!queued) done.SetResult(new CommandResponse { Error = "Envy is quitting.", ExitCode = 1 });
                return done.Task;
            });
            server.Start();
            commandServer = server;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            CommandProblem = $"The envy command can't reach Envy ({e.Message}). Quit and reopen Envy to try again.";
        }
    }

    public void StopCommandServer()
    {
        commandServer?.Dispose();
        commandServer = null;
    }

    /// <summary>Runs one envy command; a request for the window opens it, with the add-variable dialog for a secret.</summary>
    CommandResponse HandleCommand(CommandRequest request)
    {
        var (response, appRequest) = Store.HandleCommand(request);
        switch (appRequest)
        {
            case AppRequest.PasteSecret(var project, var key):
                _ = ShowWindowThen(project, () => Dialogs.EditVariable(this, project, null, new EnvVar(key, isSecret: true), requested: true));
                break;
            case AppRequest.Show(var project):
                _ = ShowWindowThen(project, null);
                break;
        }
        return response;
    }

    async Task ShowWindowThen(Guid? project, Func<Task>? then)
    {
        if (project is { } id && Store.Config.Find(id) is not null) Window?.Select(id);
        App.Current.ShowMainWindow();
        // A window that has never been shown (Envy started in the notification area) has no XamlRoot for a dialog yet.
        for (var i = 0; i < 50 && XamlRoot is null; i++) await Task.Delay(100);
        if (then is not null) await then();
    }

    // Coding agents

    /// <summary>The instruction files for agents found here: on a shell's PATH, or with their settings folder.</summary>
    public List<AgentTarget> PresentAgents => Store.AgentTargets.Where(Store.IsPresent).ToList();

    /// <summary>The agents a target reaches, named by what was found ("Codex and other agents"), since one skill reaches several.</summary>
    public string AgentTitle(AgentTarget target)
    {
        var found = Store.FoundAgents(target);
        if (target.Agents.Length <= 1 || found.Count == 0) return target.Name;
        return found.Count < target.Agents.Length ? string.Join(", ", found) + " and other agents" : UI.JoinList(found);
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

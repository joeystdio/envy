using System.Reflection;
using Envy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Envy.App;

/// <summary>Back Up Everything and Restore from Backup (spec/backup-format.md), the same file as Envy for macOS.</summary>
public static partial class Dialogs
{
    static string AppName => "Envy " + (typeof(Dialogs).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "");

    // Back up

    public static async Task BackUp(AppState state)
    {
        if (state.XamlRoot is not { } root || state.Window is not { } window) return;
        var store = state.Store;
        var config = store.Config;
        var variables = config.Scopes.Sum(p => p.Variables.Count);
        var secrets = config.Scopes.Sum(p => p.Variables.Count(v => v.IsSecret));

        var password = new PasswordBox { Header = "Password", PlaceholderText = $"At least {Backup.MinimumPasswordLength} characters" };
        var again = new PasswordBox { Header = "Type it again" };
        var problem = UI.Caption("", UI.Caution);
        var panel = UI.Column(12,
            UI.Text($"Saves Global and {UI.Plural(config.Projects.Count, "project", "projects")}, with their folders and {UI.Plural(variables, "variable", "variables")}, {secrets} of them secret, in one file encrypted with this password. Envy on a Mac or another PC can restore it."),
            password, again, problem,
            UI.Caption("Envy doesn't keep this password: without it, the backup can't be opened."));
        var dialog = UI.Dialog(root, "Back Up Everything", panel, 480);
        dialog.PrimaryButtonText = "Save Backup…";
        dialog.CloseButtonText = "Cancel";

        void Validate()
        {
            var issue = Backup.PasswordProblem(password.Password, again.Password);
            dialog.IsPrimaryButtonEnabled = issue is null;
            // Only once there's something to compare, so the first field doesn't nag while you type.
            problem.Text = again.Password.Length > 0 ? issue ?? "" : "";
            problem.Visibility = problem.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        password.PasswordChanged += (_, _) => Validate();
        again.PasswordChanged += (_, _) => Validate();
        dialog.Opened += (_, _) => password.Focus(FocusState.Programmatic);
        Validate();
        if (await Show(dialog) != ContentDialogResult.Primary) return;

        var secret = password.Password;
        if (await UI.PickSaveFile(window, Backup.SuggestedFileName(DateTime.Now), "Envy backup", Backup.Extension) is not { } path) return;
        try
        {
            var content = store.BackupContent(AppName);
            var text = await Task.Run(() => Backup.Write(content, secret));
            await File.WriteAllTextAsync(path, text, PrivateFiles.Utf8);
            await UI.Alert(root, "Backup saved",
                $"Saved Global and {UI.Plural(content.Projects.Count, "project", "projects")} to {Path.GetFileName(path)}. Keep the password somewhere safe, such as your password manager: without it, this backup can't be opened.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BackupException)
        {
            await UI.Alert(root, "The backup wasn't saved", e.Message);
        }
    }

    // Restore

    /// <summary>Restores a backup the user picks; true when something was restored.</summary>
    public static async Task<bool> Restore(AppState state)
    {
        if (state.XamlRoot is not { } root || state.Window is not { } window) return false;
        if (await UI.PickFile(window, "Envy backup", Backup.Extension) is not { } path) return false;
        string text;
        try { text = await File.ReadAllTextAsync(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await UI.Alert(root, "Couldn't open the backup", e.Message);
            return false;
        }

        // The password
        var password = new PasswordBox { Header = "Password" };
        var problem = UI.Caption("", UI.Caution);
        problem.Visibility = Visibility.Collapsed;
        var opening = new ProgressRing { IsActive = false, Width = 16, Height = 16, Visibility = Visibility.Collapsed };
        var ask = UI.Dialog(root, "Restore from Backup", UI.Column(12,
            UI.Text($"Enter the password {Path.GetFileName(path)} was saved with."), password, UI.WrapRow(opening, problem)), 480);
        ask.PrimaryButtonText = "Open";
        ask.CloseButtonText = "Cancel";
        password.PasswordChanged += (_, _) => ask.IsPrimaryButtonEnabled = password.Password.Length > 0;
        ask.IsPrimaryButtonEnabled = false;
        BackupContent? content = null;
        ask.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            var secret = password.Password;
            opening.IsActive = true;
            opening.Visibility = Visibility.Visible;
            problem.Visibility = Visibility.Collapsed;
            try
            {
                content = await Task.Run(() => Backup.Read(text, secret));
            }
            catch (BackupException e)
            {
                problem.Text = e.Message;
                problem.Visibility = Visibility.Visible;
                args.Cancel = true;
                password.Focus(FocusState.Programmatic);
            }
            finally
            {
                opening.IsActive = false;
                opening.Visibility = Visibility.Collapsed;
                deferral.Complete();
            }
        };
        ask.Opened += (_, _) => password.Focus(FocusState.Programmatic);
        if (await Show(ask) != ContentDialogResult.Primary || content is null) return false;

        return await RestoreChoices(state, root, content);
    }

    /// <summary>What restoring does, Add or Replace, and what to do with expanding values from a Mac.</summary>
    static async Task<bool> RestoreChoices(AppState state, XamlRoot root, BackupContent content)
    {
        var store = state.Store;
        var from = content.Platform == Backup.MacOS ? "a Mac" : "Windows";
        var made = DateTime.TryParse(content.Created, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var created)
            ? $" on {created.ToLocalTime():d MMMM yyyy}" : "";
        var variables = content.Scopes.Sum(s => s.Variables.Count);

        var mode = new RadioButtons { Header = "Restore by", MaxColumns = 1, Items = { "Adding to what's here: nothing here is deleted", "Replacing everything here with the backup" }, SelectedIndex = 0 };
        var summary = UI.Column(6);
        var panel = UI.Column(14,
            UI.Text($"Made on {from}{made}{(content.App.Length > 0 ? $" by {content.App}" : "")}: Global and {UI.Plural(content.Projects.Count, "project", "projects")}, {UI.Plural(variables, "variable", "variables")}."),
            mode, UI.CardOf(summary, 12));

        // Expanding values from the other system
        var issues = BackupRestore.ExpandIssues(content, Backup.ThisPlatform);
        var rows = new List<(ExpandIssue Issue, ComboBox Choice, TextBox Value)>();
        if (issues.Count > 0)
        {
            var list = UI.Column(14, UI.Text($"These values expand other variables and were made for {from}. Choose what to restore for each:", bold: true));
            foreach (var issue in issues)
            {
                var choice = new ComboBox { MinWidth = 180 };
                if (issue.Suggestion is not null) choice.Items.Add("Use suggestion");
                choice.Items.Add("Keep as is");
                choice.Items.Add("Turn Expand off");
                choice.SelectedIndex = issue.DefaultAction switch
                {
                    ExpandAction.UseSuggestion => 0,
                    ExpandAction.KeepAsIs => issue.Suggestion is null ? 0 : 1,
                    _ => choice.Items.Count - 1,
                };
                var value = new TextBox { Text = issue.Suggestion ?? issue.Value, FontFamily = UI.Mono, IsEnabled = issue.Suggestion is not null };
                void Sync()
                {
                    var action = Action(issue, choice.SelectedIndex);
                    value.IsEnabled = action == ExpandAction.UseSuggestion;
                    if (action != ExpandAction.UseSuggestion) value.Text = issue.Value;
                    else if (value.Text == issue.Value) value.Text = issue.Suggestion ?? issue.Value;
                }
                choice.SelectionChanged += (_, _) => Sync();
                list.Children.Add(UI.Column(4,
                    UI.Text($"{issue.Key}  ({issue.ScopeName})", bold: true, mono: true),
                    UI.Text(issue.Value, 12, UI.Secondary, mono: true),
                    UI.Caption(issue.Reason),
                    UI.WrapRow(choice, value)));
                rows.Add((issue, choice, value));
            }
            panel.Children.Add(list);
        }

        List<ExpandFix> Fixes() => rows.Select(r => new ExpandFix(r.Issue.Scope, r.Issue.Index, Action(r.Issue, r.Choice.SelectedIndex), r.Value.Text)).ToList();
        RestoreMode Mode() => mode.SelectedIndex == 1 ? RestoreMode.Replace : RestoreMode.Add;

        var dialog = UI.Dialog(root, "Restore from Backup", panel, 620);
        dialog.PrimaryButtonText = "Restore";
        dialog.CloseButtonText = "Cancel";
        void Preview()
        {
            summary.Children.Clear();
            foreach (var line in Describe(store.PreviewRestore(content, Mode(), Fixes()), Mode(), store.Paths))
                summary.Children.Add(UI.Text(line));
            dialog.PrimaryButtonText = Mode() == RestoreMode.Replace ? "Replace Everything" : "Restore";
        }
        mode.SelectionChanged += (_, _) => Preview();
        Preview();
        if (await Show(dialog) != ContentDialogResult.Primary) return false;

        try
        {
            store.RestoreBackup(content, Mode(), Fixes());
        }
        catch (BackupException e)
        {
            await UI.Alert(root, "Nothing was restored", e.Message);
            return false;
        }
        state.Window?.Select(Project.GlobalId);
        return true;
    }

    static ExpandAction Action(ExpandIssue issue, int index)
    {
        if (issue.Suggestion is null) index++;
        return index switch { 0 => ExpandAction.UseSuggestion, 1 => ExpandAction.KeepAsIs, _ => ExpandAction.TurnOff };
    }

    /// <summary>The summary lines shown before restoring.</summary>
    static IEnumerable<string> Describe(RestoreReport report, RestoreMode mode, AppPaths paths)
    {
        if (mode == RestoreMode.Replace && report.ProjectsRemoved.Count > 0)
            yield return $"Removes {UI.Plural(report.ProjectsRemoved.Count, "project", "projects")} here: {UI.JoinList(report.ProjectsRemoved)}. Global is replaced too.";
        else if (mode == RestoreMode.Replace)
            yield return "Global is replaced.";
        if (report.ProjectsAdded.Count > 0) yield return $"Adds {UI.Plural(report.ProjectsAdded.Count, "project", "projects")}: {UI.JoinList(report.ProjectsAdded)}.";
        if (report.ProjectsMerged.Count > 0) yield return $"Merges into {UI.JoinList(report.ProjectsMerged)}; their values from the backup win.";
        yield return VariableCounts(report, mode) + " Ctrl+Z undoes it.";
        if (report.MissingFolders.Count > 0)
            yield return "These folders don't exist on this PC. They're restored anyway; link the project to the right folder later:\n"
                + string.Join("\n", report.MissingFolders.Select(m => $"   {m.Project}: {paths.Abbreviate(m.Folder)}"));
        if (report.SkippedFolders.Count > 0)
            yield return "Not linked, because another project here already has the folder:\n"
                + string.Join("\n", report.SkippedFolders.Select(s => $"   {s.Project}: {paths.Abbreviate(s.Folder)} (belongs to {s.Owner})"));
        if (report.TurnedOff.Count > 0)
            yield return $"Restored switched off, since all their folders belong to other projects: {UI.JoinList(report.TurnedOff)}.";
    }

    /// <summary>"7 variables added, 1 replaced, 1 of them secret.", leaving out the parts that are zero.</summary>
    internal static string VariableCounts(RestoreReport report, RestoreMode mode)
    {
        var parts = new List<string>();
        if (mode == RestoreMode.Replace)
        {
            if (report.VariablesAdded > 0) parts.Add(UI.Plural(report.VariablesAdded, "variable", "variables"));
        }
        else
        {
            if (report.VariablesAdded > 0) parts.Add(UI.Plural(report.VariablesAdded, "variable", "variables") + " added");
            if (report.VariablesReplaced > 0)
                parts.Add(parts.Count == 0 ? UI.Plural(report.VariablesReplaced, "variable", "variables") + " replaced" : $"{report.VariablesReplaced} replaced");
        }
        if (parts.Count == 0) return "No variables.";
        if (report.Secrets > 0) parts.Add($"{report.Secrets} of them secret");
        return string.Join(", ", parts) + ".";
    }
}

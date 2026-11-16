using Envy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Envy.App;

/// <summary>One variable in the table: on/off, name, value (masked when secret), Secret and Expand, and a note.</summary>
public sealed class VariableRow : Grid
{
    readonly AppState state;
    readonly Guid projectId;
    readonly HashSet<Guid> revealed;
    readonly CheckBox enabled = new() { MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox name = new() { FontFamily = UI.Mono, BorderThickness = new Thickness(0), Background = Transparent, VerticalAlignment = VerticalAlignment.Center };
    readonly ContentControl warning = new() { VerticalAlignment = VerticalAlignment.Center, IsTabStop = false };
    readonly Grid valueHost = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox note = new() { BorderThickness = new Thickness(0), Background = Transparent, PlaceholderText = "", VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel options = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    bool updating;

    static Brush Transparent => new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public Guid VariableId { get; }

    public static readonly GridLength[] Columns =
    [
        new(36), new(2, GridUnitType.Star), new(3, GridUnitType.Star), new(80), new(1.5, GridUnitType.Star),
    ];

    public VariableRow(AppState state, Guid projectId, Guid variableId, HashSet<Guid> revealed, ProjectPage page)
    {
        this.state = state;
        this.projectId = projectId;
        this.revealed = revealed;
        VariableId = variableId;
        foreach (var width in Columns) ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        ColumnSpacing = 8;

        Children.Add(enabled);
        var nameCell = new Grid();
        nameCell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameCell.Children.Add(name);
        SetColumn(warning, 1);
        nameCell.Children.Add(warning);
        SetColumn(nameCell, 1);
        Children.Add(nameCell);
        SetColumn(valueHost, 2);
        Children.Add(valueHost);
        SetColumn(options, 3);
        Children.Add(options);
        SetColumn(note, 4);
        Children.Add(note);
        note.Foreground = UI.Secondary;

        enabled.Click += (_, _) => state.Store.UpdateVariables(new HashSet<Guid> { VariableId }, projectId, enabled.IsChecked == true ? "Turn On" : "Turn Off", v => v.IsEnabled = enabled.IsChecked == true);
        name.TextChanged += (_, _) => { if (!updating) Edit(v => v.Key = name.Text); };
        note.TextChanged += (_, _) => { if (!updating) Edit(v => v.Note = note.Text); };
        DoubleTapped += (_, e) =>
        {
            // Double-clicking text selects a word; elsewhere in the row it opens the editor.
            for (var o = e.OriginalSource as DependencyObject; o is not null && o != this; o = VisualTreeHelper.GetParent(o))
                if (o is TextBox or PasswordBox) return;
            _ = Dialogs.EditVariable(state, projectId, VariableId);
        };
        ContextFlyout = page.ContextMenu(this);
        Update();
    }

    EnvVar? Variable => state.Store.Config.Find(projectId)?.Variables.Find(v => v.Id == VariableId);

    void Edit(Action<EnvVar> change)
    {
        state.Store.EditVariable(projectId, VariableId, change);
        RefreshWarnings();
    }

    public void Update()
    {
        if (Variable is not { } v) return;
        updating = true;
        enabled.IsChecked = v.IsEnabled;
        UI.SetToolTip(enabled, v.IsEnabled ? "Loaded. Click to turn off." : "Off. Click to load it.");
        if (name.Text != v.Key) name.Text = v.Key;
        if (note.Text != v.Note) note.Text = v.Note;
        BuildValue(v);
        BuildOptions(v);
        updating = false;
        RefreshWarnings();
    }

    void BuildValue(EnvVar v)
    {
        valueHost.Children.Clear();
        valueHost.ColumnDefinitions.Clear();
        valueHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        valueHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var isRevealed = revealed.Contains(v.Id);
        FrameworkElement field;
        if (v.Value.Contains('\n'))
        {
            var text = UI.Text(v.IsSecret && !isRevealed ? "•••••• (multi-line)" : v.Value.Replace("\r\n", "\n").Replace("\n", " ⏎ "), color: UI.Secondary, wrap: false, mono: true);
            UI.SetToolTip(text, "Multi-line value. Double-click the row to edit it.");
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(10, 0, 0, 0);
            field = text;
        }
        else if (v.IsSecret && !isRevealed)
        {
            var box = new PasswordBox { Password = v.Value, BorderThickness = new Thickness(0), Background = Transparent, PasswordRevealMode = PasswordRevealMode.Hidden };
            box.PasswordChanged += (_, _) => { if (!updating) Edit(x => x.Value = box.Password); };
            field = box;
        }
        else
        {
            var box = new TextBox { Text = v.Value, FontFamily = UI.Mono, BorderThickness = new Thickness(0), Background = Transparent, PlaceholderText = "empty" };
            if (!v.IsEnabled) box.Foreground = UI.Secondary;
            box.TextChanged += (_, _) => { if (!updating) Edit(x => x.Value = box.Text); };
            field = box;
        }
        valueHost.Children.Add(field);
        if (v.IsSecret)
        {
            var eye = UI.IconButton(isRevealed ? UI.EyeOff : UI.Eye, isRevealed ? "Hide value" : "Show value", () =>
            {
                if (!revealed.Remove(v.Id)) revealed.Add(v.Id);
                if (Variable is { } current) BuildValue(current);
            }, 12);
            eye.Foreground = UI.Secondary;
            SetColumn(eye, 1);
            valueHost.Children.Add(eye);
        }
    }

    void BuildOptions(EnvVar v)
    {
        options.Children.Clear();
        var secret = UI.IconButton(v.IsSecret ? UI.Lock : UI.Unlock,
            v.IsSecret ? "Secret: kept in Credential Manager and hidden here. Click to make it plain." : "Plain value. Click to keep it in Credential Manager and hide it.",
            () => state.Store.UpdateVariables(new HashSet<Guid> { VariableId }, projectId, v.IsSecret ? "Make Plain" : "Make Secret", x => x.IsSecret = !x.IsSecret), 13);
        secret.Foreground = v.IsSecret ? UI.Accent : UI.Tertiary;
        options.Children.Add(secret);

        var expand = new Button
        {
            Content = new TextBlock { Text = "$", FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 14 },
            Padding = new Thickness(8, 4, 8, 4),
            Background = Transparent,
            BorderThickness = new Thickness(0),
            Foreground = v.Expands ? UI.Accent : UI.Tertiary,
        };
        UI.SetToolTip(expand, v.Expands
            ? "Expands %NAME%, $NAME, ${NAME} and ~ when a shell loads it. Click to use the value literally."
            : "Literal value. Click to expand %NAME%, $NAME, ${NAME} and ~ when a shell loads it.");
        expand.Click += (_, _) => state.Store.UpdateVariables(new HashSet<Guid> { VariableId }, projectId, v.Expands ? "Use Literal Value" : "Expand Variables", x => x.Expands = !x.Expands);
        options.Children.Add(expand);
    }

    public void RefreshWarnings()
    {
        if (Variable is not { } v || state.Store.Config.Find(projectId) is not { } p) return;
        warning.Content = null;
        name.Foreground = v.HasValidKey ? (v.IsEnabled ? UI.Brush("TextFillColorPrimaryBrush") : UI.Secondary) : UI.Critical;
        if (v.Problem is { } problem)
        {
            warning.Content = Tip(UI.Icon(UI.Warning, 14, UI.Critical), $"Not loaded: {problem}");
        }
        else if (p.DuplicateKeys.Contains(v.Key))
        {
            warning.Content = Tip(UI.Icon(UI.Warning, 14, UI.Caution), $"{v.Key} is defined more than once in this project; the last one wins.");
        }
        else if (v.IsEnabled && p.IsEnabled && state.Store.Config.OtherDefinitions(v.Key, projectId) is { Count: > 0 } others)
        {
            warning.Content = Tip(UI.Icon(UI.Layers, 14, UI.Secondary),
                $"Also set in {UI.JoinList(others.Select(o => o.Name).ToList())}. Global loads first, then projects, then folder projects.");
        }
    }

    static FontIcon Tip(FontIcon icon, string text)
    {
        icon.Margin = new Thickness(4, 0, 4, 0);
        UI.SetToolTip(icon, text);
        return icon;
    }

    /// <summary>Column titles above the table, lined up with the rows.</summary>
    public static Grid HeaderRow()
    {
        var grid = new Grid { Padding = new Thickness(32, 4, 32, 4), ColumnSpacing = 8 };
        foreach (var width in Columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        string[] titles = ["", "Name", "Value", "", "Note"];
        for (var i = 0; i < titles.Length; i++)
        {
            var title = UI.Caption(titles[i]);
            title.Margin = new Thickness(10, 0, 0, 0);
            SetColumn(title, i);
            grid.Children.Add(title);
        }
        return grid;
    }
}

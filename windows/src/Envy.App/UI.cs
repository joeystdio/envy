using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Envy.App;

/// <summary>Small helpers for building the UI in code.</summary>
public static class UI
{
    // Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10) glyphs.
    public const string Globe = "", Folder = "", Package = "", Terminal = "", Add = "",
        Import = "", Export = "", More = "", Lock = "", Unlock = "", Eye = "",
        EyeOff = "", Warning = "", Delete = "", Copy = "", Chain = "", Close = "",
        Check = "", Verified = "", Refresh = "", Layers = "", Key = "", Shield = "",
        OpenFolder = "", Error = "", Info = "", Up = "", Down = "", Sparkle = "",
        Edit = "", Paste = "", Search = "", Document = "";

    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    public static Brush Secondary => Brush("TextFillColorSecondaryBrush");
    public static Brush Tertiary => Brush("TextFillColorTertiaryBrush");
    public static Brush Accent => Brush("AccentTextFillColorPrimaryBrush");
    public static Brush Success => Brush("SystemFillColorSuccessBrush");
    public static Brush Caution => Brush("SystemFillColorCautionBrush");
    public static Brush Critical => Brush("SystemFillColorCriticalBrush");
    public static Brush Card => Brush("CardBackgroundFillColorDefaultBrush");
    public static Brush CardStroke => Brush("CardStrokeColorDefaultBrush");

    public static TextBlock Text(string text, double size = 14, Brush? color = null, bool bold = false, bool wrap = true, bool mono = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (!wrap) t.TextTrimming = TextTrimming.CharacterEllipsis;
        if (color is not null) t.Foreground = color;
        if (bold) t.FontWeight = FontWeights.SemiBold;
        if (mono) t.FontFamily = Mono;
        return t;
    }

    public static TextBlock Title(string text) => Text(text, 28, bold: true);
    public static TextBlock Subtitle(string text) => Text(text, 20, bold: true);
    public static TextBlock Caption(string text, Brush? color = null) => Text(text, 12, color ?? Secondary);

    public static FontIcon Icon(string glyph, double size = 16, Brush? color = null)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = size };
        if (color is not null) icon.Foreground = color;
        return icon;
    }

    public static StackPanel Row(double spacing, params UIElement[] children) => Stack(Orientation.Horizontal, spacing, children);
    public static StackPanel Column(double spacing, params UIElement[] children) => Stack(Orientation.Vertical, spacing, children);

    static StackPanel Stack(Orientation orientation, double spacing, UIElement[] children)
    {
        var panel = new StackPanel { Orientation = orientation, Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        if (orientation == Orientation.Horizontal) panel.VerticalAlignment = VerticalAlignment.Center;
        return panel;
    }

    public static Button Button(string text, Action onClick, bool accent = false, string? glyph = null)
    {
        var button = new Button { Content = glyph is null ? text : Row(8, Icon(glyph, 14), Text(text, wrap: false)) };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Button IconButton(string glyph, string tooltip, Action onClick, double size = 14)
    {
        var button = new Button
        {
            Content = Icon(glyph, size),
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    public static HyperlinkButton Link(string text, Action onClick)
    {
        var link = new HyperlinkButton { Content = text, Padding = new Thickness(0) };
        link.Click += (_, _) => onClick();
        return link;
    }

    /// <summary>A rounded card, the Fluent way to group settings.</summary>
    public static Border CardOf(UIElement child, double padding = 16) => new()
    {
        Child = child,
        Background = Card,
        BorderBrush = CardStroke,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(padding),
    };

    /// <summary>Text the user can select and copy, in a monospace font on a subtle background.</summary>
    public static Border Code(string text, bool select = true)
    {
        var block = new TextBlock { Text = text, FontFamily = Mono, FontSize = 13, IsTextSelectionEnabled = select, TextWrapping = TextWrapping.Wrap };
        return new Border
        {
            Child = block,
            Background = Brush("SubtleFillColorSecondaryBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
        };
    }

    /// <summary>A command with a copy button, like "envy status".</summary>
    public static StackPanel CopyableCommand(string command) =>
        Row(4, Code(command), IconButton(Copy, "Copy", () => CopyText(command)));

    public static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    public static async Task<string?> ClipboardText()
    {
        var content = Clipboard.GetContent();
        return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
    }

    public static void ShowInExplorer(string path)
    {
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{(Directory.Exists(path) ? path : Path.GetDirectoryName(path))}\"";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    public static void OpenInNotepad(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });

    // Pickers need the window they belong to, since an unpackaged app has no CoreWindow.

    public static async Task<string?> PickFolder(Window owner)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public static async Task<string?> PickFile(Window owner)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static async Task<string?> PickSaveFile(Window owner, string suggestedName)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add("Environment file", [".env"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        return (await picker.PickSaveFileAsync())?.Path;
    }

    /// <summary>A dialog wide enough for tables; ContentDialog is otherwise capped at 548 pixels.</summary>
    public static ContentDialog Dialog(XamlRoot root, string title, UIElement content, double width = 640)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) },
            DefaultButton = ContentDialogButton.Primary,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        dialog.Resources["ContentDialogMaxWidth"] = width + 48;
        dialog.Resources["ContentDialogMinWidth"] = Math.Min(width, 548) + 48;
        dialog.Resources["ContentDialogMaxHeight"] = 900.0;
        if (content is FrameworkElement element) element.Width = width;
        return dialog;
    }

    public static async Task<bool> Confirm(XamlRoot root, string title, string message, string action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = message,
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task Alert(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        await dialog.ShowAsync();
    }

    public static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    public static string JoinList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    public static void SetToolTip(DependencyObject element, string text) => ToolTipService.SetToolTip(element, text);

    public static FlyoutBase? FlyoutOf(UIElement element) => FlyoutBase.GetAttachedFlyout((FrameworkElement)element);
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RigControl;

/// <summary>Small dialogs shared by pages (naming a lighting or cooling profile).</summary>
static class Dialogs
{
    /// <summary>Asks for a name; null when cancelled or left empty.</summary>
    public static async Task<string?> AskName(XamlRoot root, string title, string initial)
    {
        var box = new TextBox { Text = initial, SelectionStart = 0, SelectionLength = initial.Length };
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = box,
            PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }

    /// <summary>The name, or "name 2", "name 3"… when it's taken.</summary>
    public static string Unique(string name, IEnumerable<string> taken)
    {
        var set = taken.ToHashSet();
        if (!set.Contains(name)) return name;
        for (int i = 2; ; i++) if (!set.Contains($"{name} {i}")) return $"{name} {i}";
    }
}

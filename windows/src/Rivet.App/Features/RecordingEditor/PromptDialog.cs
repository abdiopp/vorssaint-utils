// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>A small modal text prompt ("Save current preset…": a 260-wide name field with Save and Cancel).</summary>
internal static class PromptDialog
{
    public static async Task<string?> ShowAsync(Window owner, string title, string placeholder, string confirm, string cancel)
    {
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var field = new TextBox { Width = 260, PlaceholderText = placeholder, MaxLength = 80 };
        var ok = new Button { Content = confirm, IsDefault = true, Classes = { "accent" }, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        var no = new Button { Content = cancel, IsCancel = true, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        string? result = null;
        ok.Click += (_, _) =>
        {
            result = field.Text?.Trim();
            dialog.Close();
        };
        no.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                field,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, ok } },
            },
        };
        dialog.Opened += (_, _) => field.Focus();
        await dialog.ShowDialog(owner);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}

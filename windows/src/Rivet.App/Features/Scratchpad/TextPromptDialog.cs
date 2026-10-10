// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Rivet.App.Features.Scratchpad;

/// <summary>A small modal text prompt (rename a tab, name a profile).</summary>
public static class TextPromptDialog
{
    public static async Task<string?> ShowAsync(Window? owner, string title, string initial, string confirm, string cancel, string? placeholder = null, int maxLength = 0)
    {
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            Topmost = owner?.Topmost ?? true,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        var box = new TextBox { Text = initial, Width = 300, PlaceholderText = placeholder, MaxLength = maxLength };
        string? result = null;
        var ok = new Button { Content = confirm, IsDefault = true, Classes = { "accent" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var no = new Button { Content = cancel, IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) =>
        {
            result = box.Text ?? string.Empty;
            dialog.Close();
        };
        no.Click += (_, _) => dialog.Close();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                result = box.Text ?? string.Empty;
                dialog.Close();
            }
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, ok } },
            },
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        if (owner is not null)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }

        return result;
    }
}

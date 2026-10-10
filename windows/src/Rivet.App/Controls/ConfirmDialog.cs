// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Rivet.App.Controls;

/// <summary>A small modal confirmation (title, message, confirm/cancel).</summary>
public static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(Window? owner, string title, string message, string confirm, string cancel, bool destructive = false)
    {
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            MaxWidth = 460,
        };
        var ok = new Button { Content = confirm, IsDefault = true, Classes = { "accent" }, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (destructive)
        {
            ok.Bind(Button.BackgroundProperty, dialog.GetResourceObservable("DangerBrush").ToBinding());
            ok.Foreground = Brushes.White;
        }

        var no = new Button { Content = cancel, IsCancel = true, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
        var result = false;
        ok.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        no.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 412 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { no, ok } },
            },
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

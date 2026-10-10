// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Rivet.App.Features.Clipboard;
using Rivet.Core.Localization;

namespace Rivet.App.Features.Toggles;

/// <summary>A small warning dialog: title, message, a destructive confirm button and Cancel (Enter / Esc).</summary>
public static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(Window? owner, string title, string message, string confirmText)
    {
        var confirmed = false;
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = owner is null,
            Topmost = true,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        var confirm = new Button { Content = confirmText, IsDefault = true, Classes = { "accent" } };
        confirm.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        var cancel = new Button { Content = L.Get("clipboard.cancel"), IsCancel = true };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        ClipboardUi.Icon("Warning", 22, "WarningBrush"),
                        new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap },
                    },
                },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, confirm } },
            },
        };
        dialog.Opened += (_, _) => confirm.Focus();
        if (owner is not null)
        {
            await dialog.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task.ConfigureAwait(true);
        }

        return confirmed;
    }
}

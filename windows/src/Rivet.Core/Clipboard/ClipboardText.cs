// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Clipboard;

/// <summary>Puts plain text on the clipboard through the lane, marked as the app's own copy.</summary>
public static class ClipboardText
{
    /// <summary>
    /// Copies <paramref name="text"/> (Command Bar answers, "Copy it", script
    /// output). The source mark makes the history record it without an app
    /// and keeps the URL cleaner from rewriting it.
    /// </summary>
    public static Task<bool> CopyAsync(ClipboardLane lane, string text)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lane.Run<bool>(
            TimeSpan.FromSeconds(5),
            (clipboard, _) => clipboard.Write(new ClipboardWriteData { Text = text }, ClipboardWriteMarks.OwnSource),
            (ok, completed) => completion.TrySetResult(completed && ok));
        return completion.Task;
    }

    /// <summary>The clipboard's plain text (not when the copy is marked concealed), or null.</summary>
    public static async Task<string?> ReadAsync(ClipboardLane lane, TimeSpan timeout)
    {
        var (content, completed) = await lane.RunAsync(timeout, c => c.Read(ClipboardReadParts.Text)).ConfigureAwait(true);
        return completed && content is { IsConcealed: false } ? content.Text : null;
    }
}

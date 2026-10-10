// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Util;

/// <summary>
/// Hook through which core code reaches the UI thread without depending on a
/// UI framework. The app installs the real dispatcher at startup; until then
/// (and in tests) work runs inline.
/// </summary>
public static class UiThread
{
    private static Func<bool> _checkAccess = static () => true;
    private static Action<Action> _post = static action => action();

    public static void Configure(Func<bool> checkAccess, Action<Action> post)
    {
        _checkAccess = checkAccess;
        _post = post;
    }

    public static bool CheckAccess() => _checkAccess();

    /// <summary>Queues <paramref name="action"/> on the UI thread.</summary>
    public static void Post(Action action) => _post(action);

    /// <summary>Runs inline when already on the UI thread, otherwise posts.</summary>
    public static void Run(Action action)
    {
        if (_checkAccess())
        {
            action();
        }
        else
        {
            _post(action);
        }
    }
}

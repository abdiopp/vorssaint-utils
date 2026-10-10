// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Rivet.Core.App;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Update;

namespace Rivet.App.Shell;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Failed,
}

/// <summary>
/// Automatic and manual update checks against GitHub Releases. When an
/// update is found the user gets one toast per version per run; installing
/// opens the release page (the signed installer handles the rest).
/// </summary>
public sealed partial class UpdateService : ObservableObject, IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly ISettingsStore _settings;
    private readonly IPlatformInfo _platform;
    private readonly INotificationService _notifications;
    private readonly IShellService _shell;
    private readonly HashSet<string> _notifiedVersions = [];
    private DispatcherTimer? _timer;

    [ObservableProperty]
    private UpdateState _state = UpdateState.Idle;

    [ObservableProperty]
    private UpdateInfo? _available;

    [ObservableProperty]
    private string? _error;

    public UpdateService(ISettingsStore settings, IPlatformInfo platform, INotificationService notifications, IShellService shell)
    {
        _settings = settings;
        _platform = platform;
        _notifications = notifications;
        _shell = shell;
    }

    public DateTimeOffset? LastChecked
    {
        get
        {
            var unix = _settings.Get(ShellSettings.UpdateLastCheckUnix);
            return unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
        }
    }

    public void StartAutomaticChecks()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _timer.Tick += (_, _) => _ = CheckIfDueAsync();
        _timer.Start();
        DispatcherTimer.RunOnce(() => _ = CheckIfDueAsync(), TimeSpan.FromSeconds(30));
    }

    public async Task CheckIfDueAsync()
    {
        if (!_settings.Get(ShellSettings.AutoCheckUpdates))
        {
            return;
        }

        if (LastChecked is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(20))
        {
            return;
        }

        await CheckAsync(automatic: true);
    }

    public async Task CheckAsync(bool automatic = false)
    {
        if (State == UpdateState.Checking)
        {
            return;
        }

        State = UpdateState.Checking;
        var includeBeta = _settings.Get(ShellSettings.IncludeBetaUpdates) || AppIdentity.IsPrerelease;
        var result = await new UpdateChecker(Http).CheckAsync(AppIdentity.Version, includeBeta, _platform.Architecture);
        _settings.Set(ShellSettings.UpdateLastCheckUnix, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        OnPropertyChanged(nameof(LastChecked));
        switch (result.Status)
        {
            case UpdateCheckStatus.Available:
                Available = result.Update;
                State = UpdateState.Available;
                if (automatic && result.Update is { } update && _notifiedVersions.Add(update.Version.ToString()))
                {
                    _notifications.Show(new NotificationRequest
                    {
                        Title = L.Get("Strings.updateNotifyTitle"),
                        Body = $"{L.Get("Strings.updateAvailablePrefix")} {update.Version}",
                        ClickActionId = "shell.openAbout",
                        Tag = "update",
                    });
                }

                break;
            case UpdateCheckStatus.UpToDate:
                Available = null;
                State = UpdateState.UpToDate;
                break;
            default:
                Error = result.Error;
                State = UpdateState.Failed;
                break;
        }
    }

    /// <summary>Opens the installer download (or the release page when the asset is missing).</summary>
    public void Install()
    {
        if (Available is { } update)
        {
            _shell.OpenUrl(update.Asset?.DownloadUrl is { Length: > 0 } url ? url : update.ReleasePageUrl);
        }
    }

    public void Dispose() => _timer?.Stop();
}

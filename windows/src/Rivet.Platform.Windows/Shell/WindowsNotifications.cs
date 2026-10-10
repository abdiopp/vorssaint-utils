// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Rivet.Platform.Windows.Shell;

/// <summary>
/// Toast notifications for an unpackaged app: the AppUserModelID is
/// registered under HKCU\Software\Classes\AppUserModelId, and clicks use a
/// per-user URI scheme (<c>&lt;id&gt;://action/&lt;actionId&gt;</c>) that relaunches
/// the exe, whose single-instance pipe forwards the action to the running copy.
/// </summary>
public sealed class WindowsNotifications : INotificationService
{
    private ToastNotifier? _notifier;
    private bool _registered;

    public event EventHandler<string>? Activated;

    public bool IsEnabled
    {
        get
        {
            try
            {
                return Notifier()?.Setting == NotificationSetting.Enabled;
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

    public void Show(NotificationRequest request)
    {
        try
        {
            var notifier = Notifier();
            if (notifier is null)
            {
                return;
            }

            var xml = new XmlDocument();
            xml.LoadXml(BuildXml(request));
            var toast = new ToastNotification(xml);
            if (request.Tag is { } tag)
            {
                toast.Tag = tag.Length > 64 ? tag[..64] : tag;
            }

            notifier.Show(toast);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Warn("notifications", "Could not show a toast.", ex);
        }
    }

    /// <summary>Called by the app when a deep link from a toast arrives.</summary>
    public void RaiseActivated(string actionId) => Activated?.Invoke(this, actionId);

    private ToastNotifier? Notifier()
    {
        if (_notifier is not null)
        {
            return _notifier;
        }

        EnsureRegistration();
        _notifier = ToastNotificationManager.CreateToastNotifier(AppIdentity.AppUserModelId);
        return _notifier;
    }

    private void EnsureRegistration()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        try
        {
            var exe = Environment.ProcessPath ?? string.Empty;
            using (var aumid = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppIdentity.AppUserModelId}"))
            {
                aumid.SetValue("DisplayName", AppIdentity.DisplayName);
                var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.png");
                if (File.Exists(icon))
                {
                    aumid.SetValue("IconUri", icon);
                }
            }

            var scheme = AppLifetime.UriScheme;
            using var protocol = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
            protocol.SetValue(string.Empty, $"URL:{AppIdentity.DisplayName}");
            protocol.SetValue("URL Protocol", string.Empty);
            using var command = protocol.CreateSubKey(@"shell\open\command");
            command.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            Log.Warn("notifications", "Could not register the notification identity.", ex);
        }
    }

    private static string BuildXml(NotificationRequest request)
    {
        static string Escape(string s) => SecurityElement.Escape(s) ?? string.Empty;
        var launch = request.ClickActionId is { } click ? $" launch=\"{Escape(AppLifetime.ActionUri(click))}\" activationType=\"protocol\"" : string.Empty;
        var image = request.ImagePath is { } path && File.Exists(path)
            ? $"<image placement=\"hero\" src=\"{Escape(new Uri(path).AbsoluteUri)}\"/>"
            : string.Empty;
        var actions = request.Buttons.Count == 0
            ? string.Empty
            : "<actions>" + string.Concat(request.Buttons.Select(b =>
                $"<action content=\"{Escape(b.Label)}\" activationType=\"protocol\" arguments=\"{Escape(AppLifetime.ActionUri(b.ActionId))}\"/>")) + "</actions>";
        return $"<toast{launch}><visual><binding template=\"ToastGeneric\"><text>{Escape(request.Title)}</text><text>{Escape(request.Body)}</text>{image}</binding></visual>{actions}</toast>";
    }
}

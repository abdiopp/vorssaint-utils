// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Principal;
using Rivet.SensorHelper;

// Rivet.SensorHelper.exe --serve --user <SID>      started by Task Scheduler as SYSTEM
// Rivet.SensorHelper.exe --install --user <SID>    started elevated by the app (UAC)
// Rivet.SensorHelper.exe --uninstall               started elevated by the app or the uninstaller
string? mode = null;
SecurityIdentifier? user = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--serve" or "--install" or "--uninstall" when mode is null:
            mode = args[i];
            break;
        case "--user" when user is null && i + 1 < args.Length && HelperIdentity.TryParseUser(args[i + 1], out user):
            i++;
            break;
        default:
            return ExitCodes.BadArguments;
    }
}

if (!OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess)
{
    // Every mode needs administrator rights; an unelevated copy must not answer on the pipe.
    return ExitCodes.NotElevated;
}

try
{
    return mode switch
    {
        "--serve" when user is not null => await SensorServer.RunAsync(user),
        "--install" when user is not null => HelperSetup.Install(user),
        "--uninstall" => HelperSetup.Uninstall(),
        _ => ExitCodes.BadArguments,
    };
}
catch (Exception ex)
{
    HelperLog.Write($"{mode} failed: {ex}");
    return ExitCodes.Failed;
}

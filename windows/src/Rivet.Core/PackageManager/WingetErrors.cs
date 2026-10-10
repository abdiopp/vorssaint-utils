// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Maintenance.PackageManager;

/// <summary>
/// winget's HRESULT-style exit codes (AppInstallerErrors.h) mapped to plain
/// text. The raw code always stays in the technical log.
/// </summary>
public static class WingetErrors
{
    public const uint InvalidArguments = 0x8A150002;
    public const uint CtrlSignal = 0x8A150005;
    public const uint DownloadFailed = 0x8A150008;
    public const uint NoApplicableInstaller = 0x8A150010;
    public const uint HashMismatch = 0x8A150011;
    public const uint NoPackagesFound = 0x8A150014;
    public const uint MultiplePackagesFound = 0x8A150016;
    public const uint RequiresAdmin = 0x8A150019;
    public const uint StoreBlockedByPolicy = 0x8A15001B;
    public const uint StoreAppBlockedByPolicy = 0x8A15001C;
    public const uint UpdateNotApplicable = 0x8A15002B;
    public const uint UpdateAllHasFailure = 0x8A15002C;
    public const uint BlockedByPolicy = 0x8A15003A;
    public const uint PackageAgreementsNotAccepted = 0x8A150041;
    public const uint SourceAgreementsNotAccepted = 0x8A150046;
    public const uint UpgradeVersionNotNewer = 0x8A15004F;
    public const uint UpgradeVersionUnknown = 0x8A150050;
    public const uint PackageAlreadyInstalled = 0x8A150061;
    public const uint PackageIsPinned = 0x8A150068;
    public const uint InstallPackageInUse = 0x8A150101;
    public const uint InstallInProgress = 0x8A150102;
    public const uint InstallFileInUse = 0x8A150103;
    public const uint InstallDiskFull = 0x8A150105;
    public const uint InstallNoNetwork = 0x8A150107;
    public const uint RebootRequiredToFinish = 0x8A150109;
    public const uint RebootRequiredForInstall = 0x8A15010A;
    public const uint InstallCancelledByUser = 0x8A15010C;
    public const uint InstallAlreadyInstalled = 0x8A15010D;
    public const uint InstallBlockedByPolicy = 0x8A15010F;
    public const uint InstallPackageInUseByApp = 0x8A150111;

    /// <summary>The operation did what was asked; Windows needs a restart to finish it.</summary>
    public static bool IsSuccessWithRestart(uint code) => code == RebootRequiredToFinish;

    /// <summary>A string key describing <paramref name="code"/>, or null for codes without a plain-text message.</summary>
    public static string? MessageKey(uint code) => code switch
    {
        InvalidArguments => "win.packageManager.errorArguments",
        CtrlSignal or InstallCancelledByUser => "win.packageManager.errorCancelled",
        DownloadFailed => "win.packageManager.errorDownload",
        NoApplicableInstaller => "win.packageManager.errorNoInstaller",
        HashMismatch => "win.packageManager.errorHash",
        NoPackagesFound => "win.packageManager.errorNotFound",
        MultiplePackagesFound => "win.packageManager.errorMultiple",
        RequiresAdmin => "win.packageManager.errorAdmin",
        StoreBlockedByPolicy or StoreAppBlockedByPolicy or BlockedByPolicy or InstallBlockedByPolicy => "win.packageManager.errorPolicy",
        UpdateNotApplicable or UpgradeVersionNotNewer => "win.packageManager.errorNoUpdate",
        UpgradeVersionUnknown => "win.packageManager.errorVersionUnknown",
        UpdateAllHasFailure => "win.packageManager.errorSomeFailed",
        PackageAgreementsNotAccepted or SourceAgreementsNotAccepted => "win.packageManager.errorAgreements",
        PackageAlreadyInstalled or InstallAlreadyInstalled => "win.packageManager.errorAlreadyInstalled",
        PackageIsPinned => "win.packageManager.errorPinned",
        InstallPackageInUse or InstallFileInUse or InstallPackageInUseByApp => "win.packageManager.errorInUse",
        InstallInProgress => "win.packageManager.errorBusy",
        InstallDiskFull => "win.packageManager.errorDiskFull",
        InstallNoNetwork => "win.packageManager.errorNetwork",
        RebootRequiredToFinish or RebootRequiredForInstall => "win.packageManager.errorRestart",
        _ => null,
    };
}

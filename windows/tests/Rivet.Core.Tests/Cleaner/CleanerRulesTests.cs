// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Rivet.Core.Maintenance.Cleaner;
using Xunit;

namespace Rivet.Core.Tests.Cleaner;

public class CleanerRulesTests
{
    [Theory]
    [InlineData("Screenshot (1).png", true)]
    [InlineData("Screenshot (128).png", true)]
    [InlineData("Screenshot (1) copy.png", false)]
    [InlineData("screenshot (1).png", false)]
    [InlineData("Screenshot (1).jpg", false)]
    [InlineData("My Screenshot (1).png", false)]
    public void Numbered_screenshot_names_are_the_windows_default(string name, bool expected) =>
        Assert.Equal(expected, CleanerRules.IsDefaultScreenshotName(name, new DateTime(2026, 1, 1)));

    [Fact]
    public void Dated_screenshot_names_must_match_the_creation_time()
    {
        var created = new DateTime(2026, 3, 14, 10, 15, 40);
        Assert.True(CleanerRules.IsDefaultScreenshotName("Screenshot 2026-03-14 101540.png", created));
        Assert.True(CleanerRules.IsDefaultScreenshotName("Screenshot 2026-03-14 101540 (2).png", created));
        Assert.False(CleanerRules.IsDefaultScreenshotName("Screenshot 2026-03-14 101540.png", created.AddDays(2)));
        Assert.False(CleanerRules.IsDefaultScreenshotName("Screenshot 2026-03-14 101540 edited.png", created));
        Assert.False(CleanerRules.IsDefaultScreenshotName("Screenshot 2026-13-14 101540.png", created));
    }

    [Fact]
    public void Forgotten_uses_creation_and_modification_only()
    {
        var now = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
        var entry = new FsEntry("/x/Screenshot (1).png", "Screenshot (1).png", EntryFlags.None, 10, now.AddDays(-31), now.AddDays(-40));
        Assert.True(CleanerRules.IsForgotten(entry, 30, now));
        Assert.False(CleanerRules.IsForgotten(entry with { LastWriteUtc = now.AddDays(-2) }, 30, now));
        Assert.False(CleanerRules.IsForgotten(entry, 0, now));
    }

    [Theory]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe", true)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0", true)]
    [InlineData("5319275A.WhatsAppDesktop_cv1g1gvanyjgm", true)]
    [InlineData("windows_ie_ac_001", false)]
    [InlineData("Microsoft.Contoso_8wekyb3d8bbwi", false)] // 'i' is not Crockford base32
    [InlineData("Microsoft.Contoso_8wekyb3d8bbw", false)]
    [InlineData("S-1-5-21-1000", false)]
    public void Package_family_names_need_the_publisher_id_shape(string name, bool expected) =>
        Assert.Equal(expected, CleanerRules.IsPackageFamilyName(name));

    [Fact]
    public void Temp_entries_need_24_hours_without_changes()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(CleanerRules.IsStaleTemp(now.AddHours(-25), now.AddDays(-3), now));
        Assert.False(CleanerRules.IsStaleTemp(now.AddHours(-23), now.AddDays(-3), now));
        Assert.False(CleanerRules.IsStaleTemp(now.AddDays(-3), now.AddHours(-1), now));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Contoso\\contoso.exe\" /tray", "C:\\Program Files\\Contoso\\contoso.exe")]
    [InlineData("C:\\Program Files\\Contoso\\contoso.exe /tray", "C:\\Program Files\\Contoso\\contoso.exe")]
    [InlineData("C:\\Tools\\agent.exe", "C:\\Tools\\agent.exe")]
    [InlineData("rundll32.exe \"C:\\Program Files\\Contoso\\helper.dll\",Start", null)]
    [InlineData("C:\\Windows\\System32\\rundll32.exe \"C:\\Program Files\\Contoso\\helper.dll\",Start", "C:\\Program Files\\Contoso\\helper.dll")]
    [InlineData("C:\\Windows\\System32\\cmd.exe /c \"C:\\Scripts\\start.bat\"", "C:\\Scripts\\start.bat")]
    [InlineData("C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -NoProfile -File \"C:\\Scripts\\up.ps1\"", "C:\\Scripts\\up.ps1")]
    [InlineData("C:\\Windows\\System32\\wscript.exe \"C:\\Scripts\\run.vbs\" /q", "C:\\Scripts\\run.vbs")]
    [InlineData("OneDrive.exe /background", null)]
    [InlineData("", null)]
    public void Startup_commands_resolve_to_the_files_they_run(string command, string? expected)
    {
        var files = CleanerRules.ExecutablesOf(command, s => s, _ => false);
        if (expected is null)
        {
            Assert.Empty(files);
        }
        else
        {
            Assert.Equal([expected], files);
        }
    }

    [Fact]
    public void Startup_entries_are_orphaned_only_when_every_file_is_missing_from_a_fixed_drive()
    {
        const string windows = "C:\\Windows";
        Assert.True(CleanerRules.IsOrphanedCommand(["C:\\Gone\\app.exe"], _ => false, _ => DriveKind.Fixed, windows));
        Assert.False(CleanerRules.IsOrphanedCommand(["C:\\Here\\app.exe"], _ => true, _ => DriveKind.Fixed, windows));
        Assert.False(CleanerRules.IsOrphanedCommand(["E:\\Portable\\app.exe"], _ => false, _ => DriveKind.Removable, windows));
        Assert.False(CleanerRules.IsOrphanedCommand(["Z:\\Share\\app.exe"], _ => false, _ => DriveKind.Unavailable, windows));
        Assert.False(CleanerRules.IsOrphanedCommand(["C:\\Windows\\System32\\gone.exe"], _ => false, _ => DriveKind.Fixed, windows));
        Assert.False(CleanerRules.IsOrphanedCommand([], _ => false, _ => DriveKind.Fixed, windows));
    }

    [Fact]
    public void Per_user_program_folders_need_no_entry_no_program_and_a_quiet_week()
    {
        var now = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
        const string folder = "C:\\Users\\a\\AppData\\Local\\Programs\\Gone";
        Assert.True(CleanerRules.IsOrphanedProgramsFolder(folder, ["C:\\Program Files\\Other"], containsExecutable: false, now.AddDays(-30), now));
        Assert.False(CleanerRules.IsOrphanedProgramsFolder(folder, [folder + "\\app"], false, now.AddDays(-30), now));
        Assert.False(CleanerRules.IsOrphanedProgramsFolder(folder, [folder.ToUpperInvariant()], false, now.AddDays(-30), now));
        Assert.False(CleanerRules.IsOrphanedProgramsFolder(folder, [], containsExecutable: true, now.AddDays(-30), now));
        Assert.False(CleanerRules.IsOrphanedProgramsFolder(folder, [], false, now.AddDays(-2), now));
    }

    [Fact]
    public void Backup_info_reads_xml_plists_only()
    {
        const string plist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Build Version</key><string>21F90</string>
              <key>Device Name</key><string>Alex's iPhone</string>
              <key>Last Backup Date</key><date>2025-11-02T18:04:11Z</date>
            </dict>
            </plist>
            """;
        var (device, date) = CleanerRules.ReadBackupInfo(plist);
        Assert.Equal("Alex's iPhone", device);
        Assert.Equal(new DateTime(2025, 11, 2, 18, 4, 11, DateTimeKind.Utc), date);
        Assert.Equal((null, null), CleanerRules.ReadBackupInfo("bplist00\u0001\u0002"));
        Assert.Equal((null, null), CleanerRules.ReadBackupInfo("<plist><dict>"));
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(532, "532 bytes")]
    [InlineData(1340, "1.30 KB")]
    [InlineData(23506, "22.9 KB")]
    [InlineData(2400016, "2.28 MB")]
    [InlineData(2400000000, "2.23 GB")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(2047, "1.99 KB")]
    public void Sizes_format_like_explorer(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes, CultureInfo.InvariantCulture));

    [Fact]
    public void Windows_style_paths_normalize_on_any_os()
    {
        Assert.Equal("C:\\Users\\a\\AppData", SafePaths.Normalize("C:/Users/a/./Temp/../AppData/"));
        Assert.True(SafePaths.IsInside("C:\\Users\\a\\AppData\\Local\\Temp\\x", "c:\\users\\A\\appdata"));
        Assert.False(SafePaths.IsInside("C:\\Users\\a\\AppDataX", "C:\\Users\\a\\AppData"));
        Assert.True(SafePaths.IsDirectChild("C:\\Users\\a\\Temp\\x", "C:\\Users\\a\\Temp"));
        Assert.False(SafePaths.IsDirectChild("C:\\Users\\a\\Temp\\x\\y", "C:\\Users\\a\\Temp"));
        Assert.Equal(5, SafePaths.ComponentCount("C:\\Users\\a\\Temp\\x"));
        Assert.Equal("x.exe", SafePaths.FileName("C:\\a\\x.exe"));
        Assert.True(SafePaths.IsRoot("C:\\"));
        Assert.True(SafePaths.IsRoot("\\\\server\\share"));
        Assert.Equal("~\\AppData", SafePaths.Display("C:\\Users\\a\\AppData", "C:\\Users\\a"));
    }

    [Fact]
    public void Critical_folders_are_never_removable()
    {
        var critical = CriticalPaths.For(Folders("C:\\Users\\a"));
        Assert.True(critical.Contains("C:\\"));
        Assert.True(critical.Contains("D:\\"));
        Assert.True(critical.Contains("C:\\Windows"));
        Assert.True(critical.Contains("C:\\Users\\a\\AppData\\Local\\"));
        Assert.True(critical.Contains("C:\\Users\\a\\AppData\\Local\\Packages"));
        Assert.True(critical.Contains("C:\\ProgramData\\Package Cache"));
        Assert.True(critical.Contains("C:\\Users\\a\\Downloads"));
        Assert.False(critical.Contains("C:\\Users\\a\\AppData\\Local\\Temp\\old"));
    }

    [Fact]
    public void Schedule_finds_the_next_daily_weekly_and_monthly_time()
    {
        var zone = TimeZoneInfo.Utc;
        var now = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc); // a Friday
        Assert.Equal(new DateTime(2026, 10, 10, 9, 0, 0), CleanerSchedule.NextFireUtc(Spec(ScheduleFrequency.Daily), now, zone));
        Assert.Equal(new DateTime(2026, 10, 9, 11, 30, 0), CleanerSchedule.NextFireUtc(Spec(ScheduleFrequency.Daily, 11, 30), now, zone));
        // Weekday 2 = Monday.
        Assert.Equal(new DateTime(2026, 10, 12, 9, 0, 0), CleanerSchedule.NextFireUtc(Spec(ScheduleFrequency.Weekly), now, zone));
        Assert.Equal(new DateTime(2026, 11, 1, 9, 0, 0), CleanerSchedule.NextFireUtc(Spec(ScheduleFrequency.Monthly), now, zone));
        Assert.Equal(new DateTime(2026, 10, 9, 10, 0, 0).AddDays(0), CleanerSchedule.NextFireUtc(Spec(ScheduleFrequency.Daily, 10, 0), now.AddMinutes(-1), zone));
        Assert.Null(CleanerSchedule.NextFireUtc(Spec(ScheduleFrequency.Off), now, zone));
    }

    [Fact]
    public void Schedule_moves_out_of_daylight_saving_gaps_and_takes_the_first_repeated_hour()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2000, 1, 1), DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Europe", TimeSpan.FromHours(1), "Test", "Test", "Test DST", [rule]);

        // 29 March 2026: 02:30 local does not exist; the run moves to 03:00 local (01:00 UTC).
        var gap = CleanerSchedule.NextFireUtc(Spec(ScheduleFrequency.Daily, 2, 30), new DateTime(2026, 3, 28, 12, 0, 0, DateTimeKind.Utc), zone);
        Assert.Equal(new DateTime(2026, 3, 29, 1, 0, 0), gap);

        // 25 October 2026: 02:30 local happens twice; the first one is 00:30 UTC.
        var repeated = CleanerSchedule.NextFireUtc(Spec(ScheduleFrequency.Daily, 2, 30), new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc), zone);
        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0), repeated);
    }

    [Fact]
    public void A_missed_run_catches_up_two_minutes_after_launch()
    {
        var zone = TimeZoneInfo.Utc;
        var now = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);
        var spec = Spec(ScheduleFrequency.Daily);
        Assert.Equal(now.AddSeconds(120), CleanerSchedule.DueUtc(spec, now.AddDays(-2), now, zone));
        Assert.Equal(new DateTime(2026, 10, 10, 9, 0, 0), CleanerSchedule.DueUtc(spec, now.AddHours(-0.5), now, zone));
        // Never ran: no surprise clean on first enable.
        Assert.Equal(new DateTime(2026, 10, 10, 9, 0, 0), CleanerSchedule.DueUtc(spec, null, now, zone));
    }

    [Theory]
    [InlineData(12, false, 0)]
    [InlineData(12, true, 12)]
    [InlineData(1, false, 1)]
    [InlineData(11, true, 23)]
    public void Twelve_hour_pickers_convert(int h12, bool pm, int h24)
    {
        Assert.Equal(h24, CleanerSchedule.To24Hour(h12, pm));
        Assert.Equal((h12, pm), CleanerSchedule.To12Hour(h24));
    }

    [Fact]
    public void Shortcut_targets_come_from_the_unicode_link_info()
    {
        var target = ShellLink.Read(Shortcut("C:\\Program Files\\Gone\\gone.exe", unicode: true, arguments: "--tray"))!;
        Assert.Equal("C:\\Program Files\\Gone\\gone.exe", target.Path);
        Assert.Equal("--tray", target.Arguments);
        Assert.False(target.IsUncertain);

        var ansi = ShellLink.Read(Shortcut("C:\\Programme\\Größe\\app.exe", unicode: false))!;
        Assert.True(ansi.IsUncertain);

        Assert.True(ShellLink.Read(Shortcut("C:\\x.exe", unicode: true, advertised: true))!.IsAdvertised);
        Assert.Null(ShellLink.Read(Encoding.ASCII.GetBytes("not a shortcut at all, really not")));
        Assert.Null(ShellLink.Read([0x4C, 0, 0, 0]));
    }

    [Fact]
    public void Registry_backups_use_the_regedit_format()
    {
        var text = RegFile.ForValues("HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
        [
            new RegValue("Contoso", 1, "\"C:\\Program Files\\Contoso\\contoso.exe\" /tray"),
            new RegValue("Path", 2, "%LOCALAPPDATA%\\x"),
            new RegValue("Flags", 4, -1),
            new RegValue(string.Empty, 1, "default"),
        ]);
        var lines = text.Split("\r\n");
        Assert.Equal("Windows Registry Editor Version 5.00", lines[0]);
        Assert.Equal("[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]", lines[2]);
        Assert.Equal("\"Contoso\"=\"\\\"C:\\\\Program Files\\\\Contoso\\\\contoso.exe\\\" /tray\"", lines[3]);
        Assert.StartsWith("\"Path\"=hex(2):25,00,4c,00", lines[4], StringComparison.Ordinal);
        Assert.EndsWith(",00,00", lines[4], StringComparison.Ordinal);
        Assert.Equal("\"Flags\"=dword:ffffffff", lines[5]);
        Assert.Equal("@=\"default\"", lines[6]);
        Assert.Equal([0xFF, 0xFE], RegFile.Encode("x").Take(2));
    }

    [Fact]
    public void Registry_backups_include_subkeys_and_binary_values()
    {
        var key = new RegKeySnapshot("HKLM\\SOFTWARE\\Contoso\\App",
            [new RegValue("Blob", 3, new byte[] { 1, 2, 255 }), new RegValue("Big", 11, 5L), new RegValue("List", 7, new[] { "a", "b" })],
            [new RegKeySnapshot("HKLM\\SOFTWARE\\Contoso\\App\\Sub", [new RegValue("X", 1, "y")], [])]);
        var text = RegFile.ForKeys([key]);
        Assert.Contains("[HKEY_LOCAL_MACHINE\\SOFTWARE\\Contoso\\App]", text, StringComparison.Ordinal);
        Assert.Contains("\"Blob\"=hex:01,02,ff", text, StringComparison.Ordinal);
        Assert.Contains("\"Big\"=hex(b):05,00,00,00,00,00,00,00", text, StringComparison.Ordinal);
        Assert.Contains("\"List\"=hex(7):61,00,00,00,62,00,00,00,00,00", text, StringComparison.Ordinal);
        Assert.Contains("[HKEY_LOCAL_MACHINE\\SOFTWARE\\Contoso\\App\\Sub]", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WhatsApp Image 2024-03-04 at 12.34.56_ab12cd34.jpg", true)]
    [InlineData("WhatsApp Video 2024-03-04 at 9.04.56.mp4", true)]
    [InlineData("WhatsApp Ptt 2024-03-04 at 12.34.56.opus", true)]
    [InlineData("WhatsApp Image 2024-03-04 at 12.34.56 PM.jpeg", true)]
    [InlineData("Holiday WhatsApp Image 2024-03-04 at 12.34.56.jpg", false)]
    [InlineData("WhatsApp Image.jpg", false)]
    public void WhatsApp_default_names_are_recognized(string name, bool expected) =>
        Assert.Equal(expected, WhatsAppRules.HasDefaultName(name));

    [Fact]
    public void WhatsApp_web_mark_must_name_a_whatsapp_host()
    {
        const string mark = "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://web.whatsapp.com/\r\nHostUrl=https://mmg.whatsapp.net/v/t62.7119-24/file.enc\r\n";
        Assert.Equal("mmg.whatsapp.net", WhatsAppRules.WebMarkHost(mark));
        Assert.True(WhatsAppRules.IsWhatsAppHost(WhatsAppRules.WebMarkHost(mark)));
        Assert.False(WhatsAppRules.IsWhatsAppHost("evilwhatsapp.net"));
        Assert.False(WhatsAppRules.IsWhatsAppHost("whatsapp.net.example.com"));
        Assert.Null(WhatsAppRules.WebMarkHost("[ZoneTransfer]\r\nZoneId=3\r\n"));
    }

    [Fact]
    public void WhatsApp_selection_rules()
    {
        var now = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
        var candidate = new WhatsAppCandidate("/d/a.jpg", "a.jpg", 10, now.AddDays(-10), now.AddDays(-9), WhatsAppCategory.Image, WhatsAppEvidence.WebMark, new FileIdentity(1, 2, 3));
        var types = WhatsAppRules.ParseCategories("image,video,audio");
        Assert.True(WhatsAppRules.IsPreselected(candidate, types, 7, now));
        Assert.False(WhatsAppRules.IsPreselected(candidate with { Evidence = WhatsAppEvidence.NameOnly }, types, 7, now));
        Assert.False(WhatsAppRules.IsPreselected(candidate with { ModifiedUtc = now.AddDays(-1) }, types, 7, now));
        Assert.False(WhatsAppRules.IsPreselected(candidate with { Category = WhatsAppCategory.Document }, types, 7, now));
        Assert.Empty(WhatsAppRules.ParseCategories(""));
        Assert.Equal("image,audio", WhatsAppRules.FormatCategories([WhatsAppCategory.Image, WhatsAppCategory.Audio]));
        Assert.Equal(WhatsAppCategory.Archive, WhatsAppRules.CategoryOf("x.ZIP"));
        Assert.Equal(WhatsAppCategory.Document, WhatsAppRules.CategoryOf("x.pdf"));
        Assert.Equal(7, WhatsAppRules.SanitizeRetention(3));
        Assert.True(WhatsAppRules.IsPartialDownload("movie.mp4.crdownload"));
    }

    [Fact]
    public void Groups_split_safe_and_optional()
    {
        Assert.Equal(CleanerGroup.SafeCaches, Item(CleanerCategory.Caches, true).Group);
        Assert.Equal(CleanerGroup.OtherCaches, Item(CleanerCategory.Caches, false).Group);
        Assert.Equal(CleanerGroup.OtherCaches, Item(CleanerCategory.Developer, false).Group);
        Assert.Equal(CleanerGroup.Developer, Item(CleanerCategory.Developer, true).Group);
        Assert.True(CleanerGroups.IsSafe(CleanerGroup.Logs));
        Assert.False(CleanerGroups.IsSafe(CleanerGroup.RecycleBin));
    }

    internal static CleanerFolders Folders(string profile, string windows = "C:\\Windows", string programData = "C:\\ProgramData") => new()
    {
        UserProfile = profile,
        Temp = Path.Combine(profile, "AppData", "Local", "Temp"),
        LocalAppData = Path.Combine(profile, "AppData", "Local"),
        RoamingAppData = Path.Combine(profile, "AppData", "Roaming"),
        LocalLow = Path.Combine(profile, "AppData", "LocalLow"),
        ProgramData = programData,
        Windows = windows,
        ProgramFiles = "C:\\Program Files",
        ProgramFilesX86 = "C:\\Program Files (x86)",
        StartMenuPrograms = Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"),
        CommonStartMenuPrograms = Path.Combine(programData, "Microsoft", "Windows", "Start Menu", "Programs"),
        Startup = Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup"),
        CommonStartup = Path.Combine(programData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup"),
        Desktop = Path.Combine(profile, "Desktop"),
        Documents = Path.Combine(profile, "Documents"),
        Downloads = Path.Combine(profile, "Downloads"),
        Pictures = Path.Combine(profile, "Pictures"),
        Screenshots = Path.Combine(profile, "Pictures", "Screenshots"),
    };

    /// <summary>A minimal .lnk: header, LinkInfo with a local base path, optional arguments.</summary>
    internal static byte[] Shortcut(string target, bool unicode, string? arguments = null, bool advertised = false)
    {
        var flags = 0x2u | 0x80u; // HasLinkInfo | IsUnicode
        if (arguments is not null)
        {
            flags |= 0x20;
        }

        if (advertised)
        {
            flags |= 0x1000;
        }

        var header = new byte[0x4C];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4C);
        new byte[] { 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 }.CopyTo(header, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), flags);

        var headerSize = unicode ? 0x24 : 0x1C;
        var volume = new byte[0x11];
        BinaryPrimitives.WriteUInt32LittleEndian(volume, 0x11);
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(12), 0x10);
        var ansi = Encoding.Latin1.GetBytes(target + "\0");
        var suffix = new byte[] { 0 };
        var wide = unicode ? Encoding.Unicode.GetBytes(target + "\0") : [];
        var wideSuffix = unicode ? new byte[] { 0, 0 } : [];
        var volumeOffset = headerSize;
        var baseOffset = volumeOffset + volume.Length;
        var suffixOffset = baseOffset + ansi.Length;
        var wideOffset = suffixOffset + suffix.Length;
        var wideSuffixOffset = wideOffset + wide.Length;
        var size = wideSuffixOffset + wideSuffix.Length;
        var info = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), (uint)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(12), (uint)volumeOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(16), (uint)baseOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(24), (uint)suffixOffset);
        if (unicode)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(28), (uint)wideOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(32), (uint)wideSuffixOffset);
        }

        volume.CopyTo(info, volumeOffset);
        ansi.CopyTo(info, baseOffset);
        suffix.CopyTo(info, suffixOffset);
        wide.CopyTo(info, wideOffset);
        wideSuffix.CopyTo(info, wideSuffixOffset);

        var stringData = new List<byte>();
        if (arguments is not null)
        {
            var chars = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(chars, (ushort)arguments.Length);
            stringData.AddRange(chars);
            stringData.AddRange(Encoding.Unicode.GetBytes(arguments));
        }

        return [.. header, .. info, .. stringData, 0, 0, 0, 0];
    }

    private static CleanerScheduleSpec Spec(ScheduleFrequency frequency, int hour = 9, int minute = 0) => new(frequency, hour, minute, 2, 1);

    private static CleanerItem Item(CleanerCategory category, bool recommended) =>
        new() { Id = "x", Category = category, Kind = CleanerItemKind.Folder, Path = "/x", Name = "x", Recommended = recommended };
}

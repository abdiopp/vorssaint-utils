// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Features;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;
using Xunit;

namespace Rivet.Core.Tests.SystemMonitor;

public class ReadoutAndAlertTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private static MonitorSnapshot Sample() => new()
    {
        CpuUsage = 0.42,
        CpuTemperature = 61,
        GpuUsage = 0.12,
        Memory = new MemoryReading { Total = 100, Used = 61, Pressure = MemoryPressure.Warning },
        NetDownBytesPerSec = 1.2 * 1024 * 1024,
        NetUpBytesPerSec = 320 * 1024,
        Power = new PowerReading { HasBattery = true, ChargePercent = 80, IsCharging = false, TimeRemainingSeconds = 13_320, SystemWatts = 8.6 },
        BatteryTemperature = 31,
        ConnectedDevices = [new UsbDevice("a", "A", null)],
        PeripheralBatteries = [new PeripheralBattery("1", "Mouse", PeripheralKind.Mouse, 24, 0), new PeripheralBattery("2", "Keys", PeripheralKind.Keyboard, 64, 0)],
    };

    [Fact]
    public void Order_parsing_expands_legacy_tokens_and_appends_missing_ones()
    {
        var order = ReadoutTokens.ParseOrder("network,temperature,unknown,cpu,network,fanSpeed");
        Assert.Equal(ReadoutToken.Network, order[0]);
        Assert.Equal([ReadoutToken.CpuTemperature, ReadoutToken.GpuTemperature, ReadoutToken.BatteryTemperature], order.Skip(1).Take(3));
        Assert.Equal(ReadoutToken.Cpu, order[4]);
        Assert.Equal(ReadoutTokens.DefaultOrder.Count, order.Count);
    }

    [Fact]
    public void Usage_and_temperature_combine_in_values_mode()
    {
        var blocks = ReadoutComposer.Compose([ReadoutToken.Cpu, ReadoutToken.CpuTemperature, ReadoutToken.Battery, ReadoutToken.BatteryTemperature], Sample(), new ReadoutStyle(), En);
        Assert.Equal(2, blocks.Count);
        Assert.Equal("42% 61°", blocks[0].Value);
        Assert.Equal("80% 31°", blocks[1].Value);
        Assert.Equal("100% 999°", blocks[0].Reserve);
    }

    [Fact]
    public void A_temperature_alone_gets_the_unit_in_its_label()
    {
        var blocks = ReadoutComposer.Compose([ReadoutToken.CpuTemperature], Sample(), new ReadoutStyle { Unit = TemperatureUnit.Fahrenheit }, En);
        Assert.Equal("CPU°F", blocks[0].Label);
        Assert.Equal("142°", blocks[0].Value);
    }

    [Fact]
    public void Bars_mode_draws_gauges_and_never_combines()
    {
        var style = new ReadoutStyle { Bars = true };
        var blocks = ReadoutComposer.Compose([ReadoutToken.Cpu, ReadoutToken.CpuTemperature, ReadoutToken.Memory], Sample(), style, En);
        Assert.Equal(3, blocks.Count);
        Assert.True(blocks[0].IsGauge);
        Assert.Equal(6 / 15.0, blocks[0].Gauge!.Value, 6);
        Assert.Equal("61°", blocks[1].Value);
        Assert.Equal(MetricTone.Normal, blocks[2].Tone);
        Assert.Equal(MetricTone.Critical, ReadoutComposer.GaugeTone(0.95, style));
        Assert.Equal(MetricTone.Elevated, ReadoutComposer.GaugeTone(0.70, style));
    }

    [Fact]
    public void Memory_keeps_its_slot_without_a_reading()
    {
        var blocks = ReadoutComposer.Compose([ReadoutToken.Memory], MonitorSnapshot.Empty, new ReadoutStyle { MemoryStyle = "both" }, En);
        Assert.Equal("--%", blocks[0].Value);
        Assert.Equal(MemoryPressure.Unknown, blocks[0].PressureDot);
    }

    [Fact]
    public void Peripheral_block_shows_the_lowest_device_and_the_count_of_others()
    {
        var blocks = ReadoutComposer.Compose([ReadoutToken.PeripheralBattery], Sample(), new ReadoutStyle(), En);
        Assert.Equal("MOU", blocks[0].Label);
        Assert.Equal("24%+1", blocks[0].Value);
    }

    [Fact]
    public void Network_block_stacks_two_lines_and_can_put_upload_first()
    {
        var down = ReadoutComposer.Compose([ReadoutToken.Network], Sample(), new ReadoutStyle(), En)[0];
        Assert.Equal("↓1.2M", down.Value);
        Assert.Equal("↑320K", down.SecondLine);
        var up = ReadoutComposer.Compose([ReadoutToken.Network], Sample(), new ReadoutStyle { UploadFirst = true, NetworkBits = true }, En)[0];
        Assert.StartsWith("↑", up.Value, StringComparison.Ordinal);
        Assert.EndsWith("Mb", up.SecondLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_readings_hide_their_blocks()
    {
        var empty = MonitorSnapshot.Empty;
        var blocks = ReadoutComposer.Compose(ReadoutTokens.DefaultOrder, empty, new ReadoutStyle(), En);
        Assert.Equal([ReadoutToken.Memory, ReadoutToken.ConnectedDevices], blocks.Select(b => b.Token));
    }

    [Fact]
    public void Tooltip_line_joins_blocks_and_fits_the_limit()
    {
        var blocks = ReadoutComposer.Compose([ReadoutToken.Cpu, ReadoutToken.Memory, ReadoutToken.Network], Sample(), new ReadoutStyle { CombineTemperatures = false }, En);
        Assert.Equal("CPU 42% · RAM 61% · ↓1.2M ↑320K", ReadoutComposer.TooltipLine(blocks));
        Assert.Equal("CPU 42%", ReadoutComposer.TooltipLine(blocks, 12));
    }

    [Fact]
    public void Readout_tokens_need_their_family_and_battery()
    {
        var store = SettingsStore.InMemory();
        var runtime = new FeatureRuntime(store);
        store.Set(MonitorSettings.ReadoutCpu, true);
        store.Set(MonitorSettings.ReadoutBattery, true);
        Assert.Equal([ReadoutToken.Cpu], ReadoutComposer.EnabledTokens(store, runtime, hasBattery: false));
        Assert.Equal([ReadoutToken.Cpu, ReadoutToken.Battery], ReadoutComposer.EnabledTokens(store, runtime, hasBattery: true));
        runtime.SetAvailable(FeatureIds.MonitorCpu, false);
        Assert.Equal([ReadoutToken.Battery], ReadoutComposer.EnabledTokens(store, runtime, hasBattery: true));
    }

    [Fact]
    public void Peripheral_merge_rules()
    {
        Assert.Equal(85, PeripheralBatteries.ParsePercent("85%"));
        Assert.Equal(85, PeripheralBatteries.ParsePercent(85.0));
        Assert.Null(PeripheralBatteries.ParsePercent(double.NaN));
        Assert.Null(PeripheralBatteries.ParsePercent(-3));
        Assert.Null(PeripheralBatteries.ParsePercent(1e9));
        Assert.Null(PeripheralBatteries.ParsePercent("full"));

        var profiler = new[] { new PeripheralBattery("aa", "AirPods Pro", PeripheralKind.Audio, 40, 0) };
        var gatt = new[]
        {
            new PeripheralBattery("aa", "AirPods Pro", PeripheralKind.Audio, 90, 0),
            new PeripheralBattery("bb", "airpods pro", PeripheralKind.Audio, 90, 0),
            new PeripheralBattery("cc", "MX Master", PeripheralKind.Mouse, 50, 0),
        };
        var merged = PeripheralBatteries.Merge(profiler, gatt);
        Assert.Equal(2, merged.Count);
        Assert.Equal(40, merged.Single(d => d.Kind == PeripheralKind.Audio).Percent);
        Assert.Equal(PeripheralKind.Mouse, PeripheralBatteries.KindFromName("MX Master 3S Mouse"));
        Assert.Equal(PeripheralKind.Audio, PeripheralBatteries.KindFromName("Galaxy Buds2"));
    }

    [Fact]
    public void Usb_exclusions_and_stable_ids()
    {
        var devices = UsbDeviceFilter.Filter(
        [
            new UsbNode { InstanceId = "root", IsRootHub = true, VendorId = 1, ProductId = 1 },
            new UsbNode { InstanceId = "hub", VendorId = 5, ProductId = 6, DeviceClass = 9 },
            new UsbNode { InstanceId = "billboard", VendorId = 5, ProductId = 7, DeviceClass = 17 },
            new UsbNode { InstanceId = "zero", VendorId = 0, ProductId = 0 },
            new UsbNode { InstanceId = "cam", VendorId = 3, ProductId = 4, IsBuiltIn = true, Product = "Camera" },
            new UsbNode { InstanceId = "fixed", VendorId = 3, ProductId = 5, IsRemovable = false },
            new UsbNode { InstanceId = "kb", VendorId = 0x46d, ProductId = 0xc31c, Product = "  Keyboard " },
            new UsbNode { InstanceId = "kb", VendorId = 0x46d, ProductId = 0xc31c, Product = "Keyboard" },
            new UsbNode { Serial = "SN1", VendorId = 2, ProductId = 2, Product = "" },
            new UsbNode { Location = "0x14100000", VendorId = 9, ProductId = 9, Product = "Audio" },
        ]);
        Assert.Equal(3, devices.Count);
        Assert.Contains(devices, d => d.Id == "046d-c31c-kb" && d.Name == "Keyboard");
        Assert.Contains(devices, d => d.Id == "SN1" && d.Name is null);
        Assert.Contains(devices, d => d.Id == "loc0x14100000");
    }

    [Fact]
    public void Core_topology_groups_by_efficiency_class()
    {
        var uniform = CoreTopologyBuilder.Build([0, 0, 0, 0]);
        Assert.Single(uniform);
        Assert.Equal(CoreClass.Uniform, uniform[0].Class);
        var hybrid = CoreTopologyBuilder.Build([2, 2, 1, 1, 0, 0]);
        Assert.Equal(CoreClass.Performance, hybrid[0].Class);
        Assert.Equal([0, 1], hybrid[0].Cores);
        Assert.Equal([2, 3, 4, 5], hybrid[1].Cores);
    }

    [Fact]
    public void Core_matrix_packs_and_splits_groups_with_one_bar_scale()
    {
        var groups = CoreTopologyBuilder.Build([.. Enumerable.Repeat(1, 16), .. Enumerable.Repeat(0, 8)]);
        var rows = CoreMatrixLayout.Layout(groups, 292);
        var segments = rows.SelectMany(r => r).ToList();
        Assert.Equal(24, segments.Sum(s => s.Cores.Count));
        Assert.All(rows, r => Assert.True(r.Sum(s => s.Width) + (CoreMatrixLayout.GroupGap * (r.Count - 1)) <= 292.001));
        // Faster classes never get narrower bars than slower ones.
        var perf = segments.Where(s => s.Group.Class == CoreClass.Performance).Min(s => s.BarWidth);
        var eff = segments.Where(s => s.Group.Class == CoreClass.Efficiency).Max(s => s.BarWidth);
        Assert.True(perf >= eff);
    }

    [Fact]
    public void Alerts_fire_once_per_cooldown_and_follow_their_rules()
    {
        var store = SettingsStore.InMemory();
        var runtime = new FeatureRuntime(store);
        var notifications = new RecordingNotifications();
        var now = 0.0;
        using var alerts = new MonitorAlertService(store, runtime, notifications, () => now);
        store.Set(MonitorSettings.AlertMemory, true);
        store.Set(MonitorSettings.AlertDisk, true);
        store.Set(MonitorSettings.AlertBattery, true);

        var snapshot = new MonitorSnapshot
        {
            HasBattery = true,
            Memory = new MemoryReading { Total = 1, Used = 1, Pressure = MemoryPressure.Critical },
            Disks =
            [
                new DiskVolume { Info = new DiskVolumeInfo { Id = "s", Name = "Small", MountPath = "S:\\", Total = 5_000_000_000, Free = 1 } },
                new DiskVolume { Info = new DiskVolumeInfo { Id = "c", Name = "Windows", MountPath = "C:\\", Total = 500_000_000_000, Free = 20_000_000_000 } },
            ],
            Power = new PowerReading { HasBattery = true, ChargePercent = 12, IsCharging = false, ExternalConnected = true },
        };
        alerts.Evaluate(snapshot);
        Assert.Equal([AlertKind.Memory, AlertKind.Disk, AlertKind.Battery], alerts.Sent.Select(s => s.Kind));
        Assert.Contains("Windows", alerts.Sent[1].Body, StringComparison.Ordinal);

        now = 60;
        alerts.Evaluate(snapshot);
        Assert.Equal(3, alerts.Sent.Count);
        now = 15 * 60 + 1;
        alerts.Evaluate(snapshot);
        Assert.Equal(6, alerts.Sent.Count);
        Assert.Equal(6, notifications.Shown.Count);

        // Charging never fires the low battery alert.
        now = 10_000;
        alerts.Evaluate(snapshot with { Power = snapshot.Power! with { IsCharging = true }, Memory = null, Disks = [] });
        Assert.Equal(6, alerts.Sent.Count);
    }

    [Fact]
    public void Cpu_alert_needs_twelve_seconds_of_fresh_reads()
    {
        var store = SettingsStore.InMemory();
        var runtime = new FeatureRuntime(store);
        using var alerts = new MonitorAlertService(store, runtime, new RecordingNotifications(), () => 0);
        store.Set(MonitorSettings.AlertCpu, true);
        alerts.Evaluate(new MonitorSnapshot { CpuUsage = 0.95, CpuUsageReadAt = 100 });
        alerts.Evaluate(new MonitorSnapshot { CpuUsage = 0.95, CpuUsageReadAt = 106 });
        Assert.Empty(alerts.Sent);
        alerts.Evaluate(new MonitorSnapshot { CpuUsage = 0.95, CpuUsageReadAt = 112 });
        Assert.Single(alerts.Sent);
    }

    [Fact]
    public void Out_of_range_alert_thresholds_fall_back_to_their_defaults()
    {
        var store = SettingsStore.InMemory();
        store.Set(MonitorSettings.AlertCpuThreshold, 42);
        Assert.Equal(90, store.Get(MonitorSettings.AlertCpuThreshold));
        store.Set(MonitorSettings.AlertCpuThreshold, 85);
        Assert.Equal(85, store.Get(MonitorSettings.AlertCpuThreshold));
        store.Set(MonitorSettings.AlertCooldownMinutes, 7);
        Assert.Equal(15, store.Get(MonitorSettings.AlertCooldownMinutes));
        store.Set(MonitorSettings.DiskEjectExcluded, [" USB ", "usb", "", "Backup"]);
        Assert.Equal(["USB", "Backup"], store.Get(MonitorSettings.DiskEjectExcluded));
    }

    private sealed class RecordingNotifications : INotificationService
    {
        public List<NotificationRequest> Shown { get; } = [];

        public bool IsEnabled => true;

        public event EventHandler<string>? Activated
        {
            add { }
            remove { }
        }

        public void Show(NotificationRequest request) => Shown.Add(request);
    }
}

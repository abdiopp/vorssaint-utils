// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Input;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Xunit;

namespace Rivet.Core.Tests.Input;

public class ClickDebounceTests
{
    private static long Ms(double ms) => InputTime.FromMs(ms);

    [Fact]
    public void A_6ms_window_accepts_a_press_6ms_after_release_and_filters_one_at_5ms()
    {
        var filter = new ClickDebounceFilter(Ms(6));
        Assert.False(filter.OnDown(ClickButton.Left, Ms(1000)));
        Assert.False(filter.OnUp(ClickButton.Left, Ms(1010)));
        Assert.True(filter.OnDown(ClickButton.Left, Ms(1015)));
        Assert.True(filter.OnUp(ClickButton.Left, Ms(1016)));

        var boundary = new ClickDebounceFilter(Ms(6));
        boundary.OnDown(ClickButton.Left, Ms(1000));
        boundary.OnUp(ClickButton.Left, Ms(1010));
        Assert.False(boundary.OnDown(ClickButton.Left, Ms(1016)));
    }

    [Fact]
    public void The_window_counts_from_the_last_accepted_up_so_bounce_chains_cannot_extend_it()
    {
        var filter = new ClickDebounceFilter(Ms(25));
        filter.OnDown(ClickButton.Left, Ms(0));
        filter.OnUp(ClickButton.Left, Ms(50));
        Assert.True(filter.OnDown(ClickButton.Left, Ms(60)));
        Assert.True(filter.OnUp(ClickButton.Left, Ms(62)));
        Assert.True(filter.OnDown(ClickButton.Left, Ms(70)));
        Assert.True(filter.OnUp(ClickButton.Left, Ms(71)));
        // 25 ms after the accepted up at 50, not after the bounces.
        Assert.False(filter.OnDown(ClickButton.Left, Ms(75)));
    }

    [Fact]
    public void Real_double_clicks_pass_at_the_default_window()
    {
        var filter = new ClickDebounceFilter(Ms(InputSettings.DefaultClickWindowMs));
        Assert.False(filter.OnDown(ClickButton.Left, Ms(0)));
        Assert.False(filter.OnUp(ClickButton.Left, Ms(60)));
        Assert.False(filter.OnDown(ClickButton.Left, Ms(140)));
        Assert.False(filter.OnUp(ClickButton.Left, Ms(200)));
    }

    [Fact]
    public void Extra_downs_while_held_share_the_press_and_an_unmatched_up_passes()
    {
        var filter = new ClickDebounceFilter(Ms(25));
        Assert.False(filter.OnUp(ClickButton.Right, Ms(5)));
        Assert.False(filter.OnDown(ClickButton.Right, Ms(10)));
        Assert.True(filter.OnDown(ClickButton.Right, Ms(12)));
        Assert.False(filter.OnUp(ClickButton.Right, Ms(80)));
    }

    [Fact]
    public void A_press_whose_up_was_lost_is_forgotten_after_a_second()
    {
        var filter = new ClickDebounceFilter(Ms(25));
        Assert.False(filter.OnDown(ClickButton.Left, Ms(0)));
        // The up went to an elevated window: the next real press still works.
        Assert.False(filter.OnDown(ClickButton.Left, Ms(5000)));
        Assert.False(filter.OnUp(ClickButton.Left, Ms(5050)));
    }

    [Fact]
    public void Buttons_are_independent_and_backwards_time_resets_a_button()
    {
        var filter = new ClickDebounceFilter(Ms(25));
        filter.OnDown(ClickButton.Left, Ms(0));
        filter.OnUp(ClickButton.Left, Ms(10));
        Assert.False(filter.OnDown(ClickButton.Middle, Ms(12)));
        Assert.True(filter.OnDown(ClickButton.Left, Ms(15)));
        Assert.True(filter.OnUp(ClickButton.Left, Ms(16)));
        Assert.False(filter.OnDown(ClickButton.Left, Ms(1)));
    }

    [Fact]
    public void Window_setting_falls_back_to_25_outside_5_to_100()
    {
        var store = SettingsStore.InMemory();
        store.Set(InputSettings.ClickDebounceWindowMs, 4);
        Assert.Equal(25, store.Get(InputSettings.ClickDebounceWindowMs));
        store.Set(InputSettings.ClickDebounceWindowMs, 100);
        Assert.Equal(100, store.Get(InputSettings.ClickDebounceWindowMs));
        store.Set(InputSettings.ClickDebounceWindowMs, 101);
        Assert.Equal(25, store.Get(InputSettings.ClickDebounceWindowMs));
    }
}

public class KeyDebounceTests
{
    private const int KeyA = 0x1E;
    private const int KeyE = 0x12;
    private const int KeyR = 0x13;

    private static long S(double seconds) => (long)(seconds * InputTime.NsPerSecond);

    [Fact]
    public void Spec_vectors_release_then_press()
    {
        var ten = new KeyDebounceFilter { GlobalWindowMs = 10 };
        ten.OnKey(KeyA, true, S(40.000));
        ten.OnKey(KeyA, false, S(40.004));
        Assert.False(ten.OnKey(KeyA, true, S(40.014)));

        var five = new KeyDebounceFilter { GlobalWindowMs = 5 };
        five.OnKey(KeyA, true, S(45.000));
        five.OnKey(KeyA, false, S(45.001));
        Assert.True(five.OnKey(KeyA, true, S(45.005)));
        Assert.False(five.OnKey(KeyA, true, S(45.006)));
    }

    [Fact]
    public void Per_key_window_overrides_the_global_one()
    {
        var filter = new KeyDebounceFilter { GlobalWindowMs = 20 };
        filter.SetOverrides(new Dictionary<int, int> { [KeyA] = 100 });
        filter.OnKey(KeyA, true, S(1.000));
        filter.OnKey(KeyA, false, S(1.010));
        Assert.True(filter.OnKey(KeyA, true, S(1.060)));
        Assert.False(filter.OnKey(KeyA, true, S(1.111)));
    }

    [Fact]
    public void Rule2_only_applies_when_no_other_key_was_accepted_in_between()
    {
        var filter = new KeyDebounceFilter { GlobalWindowMs = 50 };
        filter.OnKey(KeyE, true, S(2.000));
        filter.OnKey(KeyE, false, S(2.010));
        filter.OnKey(KeyR, true, S(2.015));
        filter.OnKey(KeyR, false, S(2.020));
        Assert.False(filter.OnKey(KeyE, true, S(2.025)));
    }

    [Fact]
    public void Rule1_drops_a_second_down_while_the_key_is_down()
    {
        var filter = new KeyDebounceFilter { GlobalWindowMs = 20 };
        Assert.False(filter.OnKey(KeyA, true, S(3.000)));
        Assert.True(filter.OnKey(KeyA, true, S(3.005)));
    }

    [Fact]
    public void Auto_repeat_is_never_filtered_and_does_not_refresh_the_press()
    {
        var filter = new KeyDebounceFilter { GlobalWindowMs = 500, RepeatThresholdNs = InputTime.FromMs(400) };
        Assert.False(filter.OnKey(KeyA, true, S(4.000)));
        Assert.False(filter.OnKey(KeyA, true, S(4.500)));
        Assert.False(filter.OnKey(KeyA, true, S(4.533)));
        Assert.False(filter.OnKey(KeyA, true, S(4.566)));
    }

    [Fact]
    public void A_suppressed_down_leaves_the_key_up_so_its_up_passes()
    {
        var filter = new KeyDebounceFilter { GlobalWindowMs = 30 };
        filter.OnKey(KeyA, true, S(5.000));
        filter.OnKey(KeyA, false, S(5.010));
        Assert.True(filter.OnKey(KeyA, true, S(5.015)));
        Assert.False(filter.OnKey(KeyA, false, S(5.020)));
    }

    [Fact]
    public void Window_zero_accepts_everything_and_stale_state_is_forgotten()
    {
        var zero = new KeyDebounceFilter { GlobalWindowMs = 0 };
        zero.OnKey(KeyA, true, S(6.000));
        zero.OnKey(KeyA, false, S(6.001));
        Assert.False(zero.OnKey(KeyA, true, S(6.002)));

        var stale = new KeyDebounceFilter { GlobalWindowMs = 500 };
        stale.OnKey(KeyA, true, S(7.000));
        Assert.False(stale.OnKey(KeyA, true, S(13.000)));
    }

    [Fact]
    public void Modifiers_lock_keys_and_unicode_text_are_never_filtered()
    {
        foreach (var vk in new[] { VirtualKeys.LShift, VirtualKeys.RControl, VirtualKeys.LMenu, VirtualKeys.LWin, VirtualKeys.Capital, 0x90, 0x91, 0xE7 })
        {
            Assert.Equal(-1, KeyDebounceFilter.KeyIdFor(vk, 0x2A, false));
        }

        Assert.Equal(0x14D, KeyDebounceFilter.KeyIdFor(VirtualKeys.Right, 0x4D, extended: true));
        Assert.Equal(0x200 + 0x41, KeyDebounceFilter.KeyIdFor(0x41, 0, false));
    }

    [Fact]
    public void macOS_overrides_decode_with_the_spec_vector_and_convert_to_scan_codes()
    {
        var mac = KeyDebounceOverrides.DecodeMacKeyCodes("37:100,bad,40:0,99:999");
        Assert.Equal(new Dictionary<int, int> { [37] = 100, [40] = 0, [99] = 5 }, mac);

        var windows = KeyDebounceOverrides.Decode("37:100,bad,40:0,99:999");
        Assert.Equal(new Dictionary<int, int> { [0x25] = 0, [0x26] = 100 }, windows);
    }

    [Fact]
    public void Scan_code_overrides_round_trip_sorted()
    {
        var encoded = KeyDebounceOverrides.Encode(new Dictionary<int, int> { [0x14D] = 40, [0x1E] = 100, [0x30] = 999 });
        Assert.Equal("sc1E:100,sc30:5,scE04D:40", encoded);
        Assert.Equal(new Dictionary<int, int> { [0x1E] = 100, [0x30] = 5, [0x14D] = 40 }, KeyDebounceOverrides.Decode(encoded));
    }

    [Fact]
    public void The_picker_offers_52_keys_and_the_global_window_is_not_clamped()
    {
        Assert.Equal(52, DebounceKeyCatalog.Keys.Count);
        Assert.Equal(52, DebounceKeyCatalog.Keys.Select(k => k.ScanId).Distinct().Count());
        var store = SettingsStore.InMemory();
        store.Set(InputSettings.KeyDebounceWindowMs, 501);
        Assert.Equal(5, store.Get(InputSettings.KeyDebounceWindowMs));
        store.Set(InputSettings.KeyDebounceWindowMs, 0);
        Assert.Equal(0, store.Get(InputSettings.KeyDebounceWindowMs));
    }
}

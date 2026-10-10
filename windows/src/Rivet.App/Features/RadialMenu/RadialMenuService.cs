// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.CleaningMode;
using Rivet.Core.Actions;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Modules.RadialMenu;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.RadialMenu;

/// <summary>
/// The radial menu (spec 07 §3.2): the profiles in <c>radialMenuProfiles</c>,
/// one global shortcut slot per wheel (through the shared shortcut manager),
/// the optional side-button trigger through the shared mouse hook, and one
/// <see cref="RadialSession"/> at a time with its session-scoped keyboard
/// and mouse handlers. The wheel never takes the foreground: keys it handles
/// (Esc, Enter, arrows, digits) are swallowed by the keyboard hook while it is
/// open, everything else reaches the app in front.
/// </summary>
public sealed class RadialMenuService : IFeatureController, IMouseButtonClaims, IDisposable
{
    /// <summary>Wheels that can have a global shortcut (one shortcut-manager role each).</summary>
    public const int ShortcutSlots = 6;

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int MaskKey = 0xE8;

    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly IInputHooks _hooks;
    private readonly IRadialPlatform _platform;
    private readonly IScreenService _screens;
    private readonly List<IDisposable> _observers = [];
    private IReadOnlyList<RadialProfile> _profiles = [];
    private IDisposable? _triggerHook;
    private IDisposable? _sessionMouse;
    private IDisposable? _sessionKeys;
    private RadialWheelWindow? _window;
    private NowPlayingCard? _card;
    private bool _available;
    private bool _suspended;
    private bool _writingSlots;
    private CancellationTokenSource? _nowPlayingCancel;

    // Hook-thread state (read by the hook handlers, written on the UI thread).
    private volatile int[] _claimedXButtons = [];
    private volatile bool _sessionOpen;
    private volatile int _heldXButton;
    private int _wheelLeft, _wheelTop, _wheelRight, _wheelBottom;
    private readonly HashSet<int> _swallowedKeys = [];
    private readonly string?[] _slotWritten = new string?[ShortcutSlots];
    private bool _swallowNextUp;

    public RadialMenuService(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsStore>();
        _hooks = services.GetRequiredService<IInputHooks>();
        _platform = services.GetRequiredService<IRadialPlatform>();
        _screens = services.GetRequiredService<IScreenService>();
        Presenter = new RadialPresenter(services);
        _observers.Add(_settings.Observe(RadialMenuSettings.ProfilesKey, () => Dispatcher.UIThread.Post(Reload)));
        for (var i = 0; i < ShortcutSlots; i++)
        {
            var slot = i;
            _observers.Add(_settings.Observe(() => Dispatcher.UIThread.Post(() => OnSlotEdited(slot)), SlotShortcut[i]));
        }

        Reload();
    }

    /// <summary>Mirror of each wheel's shortcut for the shortcut manager (machine state; the profiles are the truth).</summary>
    public static readonly Setting<string>[] SlotShortcut =
        Enumerable.Range(0, ShortcutSlots).Select(i => new Setting<string>($"radialMenuWheel{i + 1}Shortcut", string.Empty, machineState: true)).ToArray();

    /// <summary>The slot has a wheel with a shortcut (keeps empty slots inactive instead of "failed").</summary>
    public static readonly Setting<bool>[] SlotActive =
        Enumerable.Range(0, ShortcutSlots).Select(i => new Setting<bool>($"radialMenuWheel{i + 1}Active", false, machineState: true)).ToArray();

    public RadialPresenter Presenter { get; }

    public IReadOnlyList<RadialProfile> Profiles => _profiles;

    public RadialSession? Session { get; private set; }

    public RadialProfile? SessionProfile { get; private set; }

    public RadialWheelWindow? Window => _window;

    public NowPlayingState NowPlaying { get; private set; } = NowPlayingState.NothingPlaying;

    public NowPlayingSnapshot? NowPlayingSnapshot { get; private set; }

    /// <summary>The window in front when the wheel opened (layout and shortcut slices act on it).</summary>
    public nint ForegroundAtOpen { get; private set; }

    /// <summary>Profiles changed (UI thread).</summary>
    public event EventHandler? ProfilesChanged;

    /// <summary>A session opened, changed or closed (UI thread).</summary>
    public event EventHandler? SessionChanged;

    /// <summary>A side button went down while the settings "Button test" listens (XButton 1/2).</summary>
    public event EventHandler<int>? ButtonTested;

    public bool IsEnabled => _available && !_suspended && _settings.Get(RadialMenuSettings.Enabled);

    // ── Lifecycle ───────────────────────────────────────────────────────

    public void Sync(bool available)
    {
        _available = available;
        Resync();
    }

    /// <summary>The single reconfiguration point: hooks and slots follow the switch, availability and profiles.</summary>
    private void Resync()
    {
        if (!IsEnabled)
        {
            Close();
        }

        SyncTriggerHook();
    }

    /// <summary>Cleaning Mode suspends the feature while it runs.</summary>
    internal void AttachCleaningMode(CleaningModeService cleaning)
    {
        cleaning.StateChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _suspended = cleaning.IsActive;
            Resync();
        });
    }

    private void SyncTriggerHook()
    {
        var buttons = IsEnabled ? _profiles.Where(p => p.MouseButton.IsSupportedOnWindows).Select(p => p.MouseButton.XButton).ToArray() : [];
        _claimedXButtons = buttons;
        var wanted = buttons.Length > 0 || ButtonTestActive;
        if (wanted && _triggerHook is null)
        {
            _triggerHook = _hooks.SubscribeMouse(OnTriggerMouse, priority: 50);
        }
        else if (!wanted && _triggerHook is not null)
        {
            _triggerHook.Dispose();
            _triggerHook = null;
        }
    }

    /// <summary>
    /// Side buttons a wheel uses as its trigger, so mouse-button shortcuts (which hook first)
    /// let them through (spec 07 §3.7.3). Called on the hook thread.
    /// </summary>
    public bool IsClaimed(int buttonId) =>
        buttonId is RadialMouseTrigger.Back or RadialMouseTrigger.Forward
        && _claimedXButtons.Contains(new RadialMouseTrigger(buttonId).XButton);

    /// <summary>The settings "Button test" listens for side buttons (the hook is kept while it does).</summary>
    public bool ButtonTestActive { get; private set; }

    public void SetButtonTest(bool active)
    {
        ButtonTestActive = active;
        SyncTriggerHook();
    }

    // ── Profiles ────────────────────────────────────────────────────────

    private void Reload()
    {
        var legacy = new RadialLegacySeeds(
            _settings.IsSaved(RadialMenuSettings.LegacyShortcut) ? _settings.Get(RadialMenuSettings.LegacyShortcut) : null,
            _settings.IsSaved(RadialMenuSettings.LegacyMouseButton) ? _settings.Get(RadialMenuSettings.LegacyMouseButton) : null,
            _settings.GetRaw(RadialMenuSettings.LegacyItemsKey));
        var result = RadialProfilesCodec.Decode(_settings.GetRaw(RadialMenuSettings.ProfilesKey), legacy);
        _profiles = result.Profiles;
        if (result.Migrated)
        {
            // Persist the seed at once, or every read would mint new ids.
            _settings.SetRaw(RadialMenuSettings.ProfilesKey, RadialProfilesCodec.Encode(_profiles));
        }

        WriteSlots();
        Resync();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves profiles (cleaned: exclusive triggers, ≤ 12 items, valid payloads).</summary>
    public void SaveProfiles(IReadOnlyList<RadialProfile> profiles)
    {
        _settings.SetRaw(RadialMenuSettings.ProfilesKey, RadialProfilesCodec.Encode(RadialProfilesCodec.Clean(profiles)));
        Reload();
    }

    public void UpdateProfile(RadialProfile profile) =>
        SaveProfiles(_profiles.Select(p => p.Id == profile.Id ? profile : p).ToList());

    /// <summary>A shortcut is refused when another wheel already uses it.</summary>
    public RadialProfile? ShortcutOwner(KeyChord chord, Guid except) =>
        _profiles.FirstOrDefault(p => p.Id != except && KeyChord.TryParse(p.Shortcut, out var c) && c == chord);

    /// <summary>Assigning a mouse button takes it away from the other wheels.</summary>
    public void AssignMouseButton(Guid profileId, RadialMouseTrigger trigger) =>
        SaveProfiles(_profiles.Select(p => p.Id == profileId
            ? p with { MouseButton = trigger }
            : !trigger.IsOff && p.MouseButton == trigger ? p with { MouseButton = RadialMouseTrigger.Off } : p).ToList());

    /// <summary>Shortcut-manager role of each slot (registered by the module).</summary>
    public static ShortcutRole SlotRole(int slot) => new()
    {
        Id = $"radialMenu.wheel{slot + 1}",
        FeatureId = FeatureIds.RadialMenu,
        TitleKey = $"win.radialMenu.wheel{slot + 1}Shortcut",
        Storage = SlotShortcut[slot],
        Default = KeyChord.None,
        RequiredEnableKeys = [FeatureKeys.RadialMenuEnabled, SlotActive[slot]],
        ActionId = SlotActionId(slot),
    };

    public static string SlotActionId(int slot) => $"radialMenu.openWheel{slot + 1}";

    /// <summary>
    /// Mirrors the wheels' shortcuts into the slots. Only slots whose wheel changed
    /// since the last write are touched, so a queued reload never overwrites an
    /// edit made on the Keyboard shortcuts page in the meantime.
    /// </summary>
    private void WriteSlots()
    {
        _writingSlots = true;
        try
        {
            for (var i = 0; i < ShortcutSlots; i++)
            {
                var shortcut = i < _profiles.Count ? _profiles[i].Shortcut : string.Empty;
                if (_slotWritten[i] == shortcut && _settings.Get(SlotActive[i]) == (shortcut.Length > 0))
                {
                    continue;
                }

                _slotWritten[i] = shortcut;
                _settings.Set(SlotShortcut[i], shortcut);
                _settings.Set(SlotActive[i], shortcut.Length > 0);
            }
        }
        finally
        {
            _writingSlots = false;
        }
    }

    /// <summary>A slot edited elsewhere (Keyboard shortcuts page) writes back into its wheel.</summary>
    private void OnSlotEdited(int slot)
    {
        if (_writingSlots || slot >= _profiles.Count)
        {
            return;
        }

        var value = RadialProfilesCodec.CleanShortcut(_settings.Get(SlotShortcut[slot]));
        if (value != _profiles[slot].Shortcut)
        {
            UpdateProfile(_profiles[slot] with { Shortcut = value });
        }
    }

    // ── Opening ─────────────────────────────────────────────────────────

    /// <summary>Wheel <paramref name="slot"/>'s shortcut fired.</summary>
    public void OnShortcut(int slot)
    {
        if (!IsEnabled || slot >= _profiles.Count)
        {
            return;
        }

        var profile = _profiles[slot];
        KeyChord.TryParse(profile.Shortcut, out var chord);
        Toggle(profile, RadialTriggerKind.Shortcut, chord, 0);
    }

    /// <summary>Settings "Try it": works with the feature off or no trigger; always sticky.</summary>
    public void TryIt(RadialProfile profile) => Open(profile, RadialTriggerKind.TryIt, KeyChord.None, 0);

    private void Toggle(RadialProfile profile, RadialTriggerKind trigger, KeyChord chord, int xButton)
    {
        // The same wheel's trigger closes it; another wheel's trigger switches.
        if (Session is not null && SessionProfile?.Id == profile.Id)
        {
            Close();
            return;
        }

        Open(profile, trigger, chord, xButton);
    }

    public bool Open(RadialProfile profile, RadialTriggerKind trigger, KeyChord chord, int xButton)
    {
        Close();
        _card?.Hide();
        var items = Presenter.Filter(profile.Items);
        if (items.Count == 0)
        {
            _platform.Beep();
            return false;
        }

        ForegroundAtOpen = _platform.ForegroundWindow();
        var pointer = _screens.CursorPosition;
        var screen = _screens.ScreenFromPoint(pointer);
        var scale = screen.Scale <= 0 ? 1 : screen.Scale;
        var work = screen.WorkArea;
        var center = RadialGeometry.Placement(_settings.Get(RadialMenuSettings.AtPointer), (pointer.X, pointer.Y), (work.X, work.Y, work.Width, work.Height), scale);
        var mode = RadialMenuSettings.ParseMode(_settings.Get(RadialMenuSettings.ActivationMode));
        var offset = ((pointer.X - center.X) / scale, (pointer.Y - center.Y) / scale);
        var session = new RadialSession(items, trigger, chord.Modifiers != KeyModifiers.None, mode, offset);
        Session = session;
        SessionProfile = profile;
        _sessionChord = chord;
        _heldXButton = trigger == RadialTriggerKind.MouseButton ? xButton : 0;
        _sessionCenter = center;
        _sessionScale = scale;

        if (ContainsNowPlaying(items))
        {
            RefreshNowPlaying();
        }
        else
        {
            NowPlaying = NowPlayingState.NothingPlaying;
            NowPlayingSnapshot = null;
        }

        var window = _window ??= new RadialWheelWindow(this);
        var half = RadialGeometry.WindowSize / 2 * scale;
        _wheelLeft = (int)Math.Round(center.X - half);
        _wheelTop = (int)Math.Round(center.Y - half);
        _wheelRight = (int)Math.Round(center.X + half);
        _wheelBottom = (int)Math.Round(center.Y + half);
        window.ShowSession(new PixelPoint((int)Math.Round(center.X), (int)Math.Round(center.Y)), scale);
        InstallSessionHooks();
        _sessionOpen = true;

        // The release can land before the hooks are installed.
        if (session.Phase == RadialPhase.Held && trigger == RadialTriggerKind.Shortcut && !ChordModifiersDown())
        {
            Apply(session.Release());
        }

        SessionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private KeyChord _sessionChord;
    private (double X, double Y) _sessionCenter;
    private double _sessionScale = 1;

    /// <summary>Closes the wheel (monitors first, then the fade).</summary>
    public void Close()
    {
        _sessionOpen = false;
        _heldXButton = 0;
        _sessionMouse?.Dispose();
        _sessionMouse = null;
        _sessionKeys?.Dispose();
        _sessionKeys = null;
        lock (_swallowedKeys)
        {
            _swallowedKeys.Clear();
        }

        if (Session is null)
        {
            return;
        }

        Session.Dismiss();
        Session = null;
        SessionProfile = null;
        _window?.CloseSession();
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Session input ───────────────────────────────────────────────────

    private void InstallSessionHooks()
    {
        _sessionMouse = _hooks.SubscribeMouse(OnSessionMouse, priority: 40);
        _sessionKeys = _hooks.SubscribeKeyboard(OnSessionKey, priority: 40);
    }

    /// <summary>Hook thread: clicks inside the wheel square are taken; anything outside dismisses and passes through.</summary>
    private bool OnSessionMouse(ref MouseHookEvent e)
    {
        if (!_sessionOpen)
        {
            return false;
        }

        var p = e.Position;
        var inside = p.X >= _wheelLeft - 2 && p.X <= _wheelRight + 2 && p.Y >= _wheelTop - 2 && p.Y <= _wheelBottom + 2;
        switch (e.Kind)
        {
            case MouseHookKind.Move:
                Dispatcher.UIThread.Post(() => PointerMoved(p));
                return false;
            case MouseHookKind.LeftDown or MouseHookKind.RightDown or MouseHookKind.MiddleDown:
                if (inside)
                {
                    _swallowNextUp = true;
                    if (e.Kind == MouseHookKind.LeftDown)
                    {
                        Dispatcher.UIThread.Post(() => Clicked(p));
                    }

                    return true;
                }

                Dispatcher.UIThread.Post(Close);
                return false;
            case MouseHookKind.LeftUp or MouseHookKind.RightUp or MouseHookKind.MiddleUp:
                if (_swallowNextUp)
                {
                    _swallowNextUp = false;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>Hook thread: Esc, Enter, arrows and digits are the wheel's; modifier releases end a held shortcut.</summary>
    private bool OnSessionKey(ref KeyboardHookEvent e)
    {
        if (!_sessionOpen)
        {
            return false;
        }

        var vk = e.VirtualKey;
        if (e.Action == KeyAction.Up)
        {
            if (IsModifier(vk))
            {
                Dispatcher.UIThread.Post(CheckHold);
            }

            lock (_swallowedKeys)
            {
                return _swallowedKeys.Remove(vk);
            }
        }

        RadialKey? key = vk switch
        {
            0x1B => RadialKey.Escape,
            0x0D => RadialKey.Enter,
            0x25 => RadialKey.Left,
            0x26 => RadialKey.Up,
            0x27 => RadialKey.Right,
            0x28 => RadialKey.Down,
            _ => null,
        };
        int? digit = vk is >= 0x31 and <= 0x39 ? vk - 0x30 : vk is >= 0x61 and <= 0x69 ? vk - 0x60 : null;
        if (key is null && digit is null)
        {
            return false;
        }

        lock (_swallowedKeys)
        {
            _swallowedKeys.Add(vk);
        }

        // A key swallowed while Alt or Win is held would leave the app seeing a lone Alt/Win
        // press: on release that opens the menu bar or the Start menu. A mask key (0xE8,
        // unassigned) in between prevents it.
        var masked = e.Modifiers.HasFlag(KeyModifiers.Alt) || e.Modifiers.HasFlag(KeyModifiers.Win);
        Dispatcher.UIThread.Post(() =>
        {
            if (masked)
            {
                _hooks.SendKeys([(MaskKey, KeyAction.Down), (MaskKey, KeyAction.Up)]);
            }

            if (Session is null)
            {
                return;
            }

            Apply(key is { } k ? Session.Key(k) : Session.Digit(digit!.Value));
        });
        return true;
    }

    /// <summary>Hook thread: claimed side buttons open their wheel; both halves of the click are always swallowed.</summary>
    private bool OnTriggerMouse(ref MouseHookEvent e)
    {
        if (e.Kind is not (MouseHookKind.XDown or MouseHookKind.XUp))
        {
            return false;
        }

        var button = e.XButton;
        if (ButtonTestActive && e.Kind == MouseHookKind.XDown)
        {
            Dispatcher.UIThread.Post(() => ButtonTested?.Invoke(this, button));
        }

        if (!_claimedXButtons.Contains(button))
        {
            return false;
        }

        if (e.Kind == MouseHookKind.XDown)
        {
            Dispatcher.UIThread.Post(() => OnTriggerDown(button));
        }
        else
        {
            Dispatcher.UIThread.Post(() => OnTriggerUp(button));
        }

        return true;
    }

    private void OnTriggerDown(int xButton)
    {
        if (!IsEnabled)
        {
            return;
        }

        var trigger = new RadialMouseTrigger(xButton == 1 ? RadialMouseTrigger.Back : RadialMouseTrigger.Forward);
        var profile = _profiles.FirstOrDefault(p => p.MouseButton == trigger);
        if (profile is null)
        {
            return;
        }

        if (Session is { } session)
        {
            // A down during a held keyboard session is ignored; otherwise it closes (or switches).
            if (session.Phase == RadialPhase.Held && session.Trigger == RadialTriggerKind.Shortcut)
            {
                return;
            }

            var same = SessionProfile?.Id == profile.Id;
            Close();
            if (same)
            {
                return;
            }
        }

        Open(profile, RadialTriggerKind.MouseButton, KeyChord.None, xButton);
    }

    private void OnTriggerUp(int xButton)
    {
        if (Session is { Trigger: RadialTriggerKind.MouseButton } session && _heldXButton == xButton)
        {
            Apply(session.Release());
        }
    }

    private void PointerMoved(PixelPoint p)
    {
        if (Session is not { } session)
        {
            return;
        }

        // Fallback for a modifier release that landed before the keyboard hook was installed.
        if (session.Phase == RadialPhase.Held && session.Trigger == RadialTriggerKind.Shortcut && !ChordModifiersDown())
        {
            Apply(session.Release());
            return;
        }

        Apply(session.PointerMoved((p.X - _sessionCenter.X) / _sessionScale, (p.Y - _sessionCenter.Y) / _sessionScale));
    }

    private void Clicked(PixelPoint p)
    {
        if (Session is not { } session)
        {
            return;
        }

        var dx = (p.X - _sessionCenter.X) / _sessionScale;
        var dy = (p.Y - _sessionCenter.Y) / _sessionScale;
        Apply(session.Click(Math.Sqrt((dx * dx) + (dy * dy))));
    }

    private void CheckHold()
    {
        if (Session is { Phase: RadialPhase.Held, Trigger: RadialTriggerKind.Shortcut } session && !ChordModifiersDown())
        {
            Apply(session.Release());
        }
    }

    /// <summary>Every modifier of the session chord is still down (extra modifiers are tolerated).</summary>
    private bool ChordModifiersDown()
    {
        var m = _sessionChord.Modifiers;
        return (!m.HasFlag(KeyModifiers.Control) || _hooks.IsKeyDown(VkControl))
               && (!m.HasFlag(KeyModifiers.Alt) || _hooks.IsKeyDown(VkMenu))
               && (!m.HasFlag(KeyModifiers.Shift) || _hooks.IsKeyDown(VkShift))
               && (!m.HasFlag(KeyModifiers.Win) || _hooks.IsKeyDown(VkLWin) || _hooks.IsKeyDown(VkRWin));
    }

    private static bool IsModifier(int vk) => vk is VkShift or VkControl or VkMenu or VkLWin or VkRWin or >= 0xA0 and <= 0xA5;

    /// <summary>Applies a session outcome: redraw, close, or close then run.</summary>
    internal void Apply(RadialOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case RadialOutcomeKind.Changed:
                _window?.Refresh();
                SessionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case RadialOutcomeKind.Close:
                Close();
                break;
            case RadialOutcomeKind.Run when outcome.Item is { } item:
                var center = new PixelPoint((int)Math.Round(_sessionCenter.X), (int)Math.Round(_sessionCenter.Y));
                var foreground = ForegroundAtOpen;
                Close();
                _ = RunItemAsync(item, foreground, center);
                break;
        }
    }

    // ── Running slices ──────────────────────────────────────────────────

    /// <summary>Runs one slice after the wheel closed (spec 07 §3.2.9).</summary>
    public async Task RunItemAsync(RadialItem item, nint foreground, PixelPoint center)
    {
        try
        {
            switch (item.Kind)
            {
                case RadialItemKind.App:
                    if (!_platform.LaunchOrActivateApp(_platform.ExpandPath(item.Payload)))
                    {
                        _platform.Beep();
                    }

                    break;
                case RadialItemKind.File:
                    if (!_platform.OpenPath(_platform.ExpandPath(item.Payload)))
                    {
                        _platform.Beep();
                    }

                    break;
                case RadialItemKind.Url:
                    if (RadialLinks.Normalize(item.Payload) is { } url)
                    {
                        _platform.OpenUrl(url);
                    }

                    break;
                case RadialItemKind.Shortcut:
                    if (KeyChord.TryParse(item.Payload, out var chord) && !chord.IsEmpty)
                    {
                        await PressAsync(chord.Modifiers, chord.VirtualKey).ConfigureAwait(true);
                    }
                    else
                    {
                        _platform.Beep();
                    }

                    break;
                case RadialItemKind.Media:
                    await RunMediaAsync(item.Payload, center).ConfigureAwait(true);
                    break;
                case RadialItemKind.Tool:
                    await Task.Delay(RadialGeometry.ToolDelay).ConfigureAwait(true);
                    if (Presenter.ResolveTool(item.Payload) is not { } tool || !await _services.GetRequiredService<ActionRegistry>().InvokeAsync(tool.Id, ActionSource.RadialMenu).ConfigureAwait(true))
                    {
                        _platform.Beep();
                    }

                    break;
                case RadialItemKind.QuickToggle:
                    await Task.Delay(RadialGeometry.ToolDelay).ConfigureAwait(true);
                    if (Presenter.ResolveQuickToggle(item.Payload) is not { } toggle || !await _services.GetRequiredService<ActionRegistry>().InvokeAsync(toggle.Id, ActionSource.RadialMenu).ConfigureAwait(true))
                    {
                        _platform.Beep();
                    }

                    break;
                case RadialItemKind.WindowLayout:
                    await Task.Delay(RadialGeometry.ToolDelay).ConfigureAwait(true);
                    if (!_platform.ApplyWindowLayout(foreground, item.Payload))
                    {
                        _platform.Beep();
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("radialMenu", $"Running a {item.Kind} slice failed.", ex);
            _platform.Beep();
        }
    }

    private async Task RunMediaAsync(string id, PixelPoint center)
    {
        var vk = id switch
        {
            RadialMediaIds.PlayPause => 0xB3,
            RadialMediaIds.NextTrack => 0xB0,
            RadialMediaIds.PreviousTrack => 0xB1,
            _ => 0,
        };
        if (vk != 0)
        {
            await PressAsync(KeyModifiers.None, vk).ConfigureAwait(true);
            return;
        }

        // Now Playing: show the card now, or when the pending reply says something plays.
        if (NowPlaying == NowPlayingState.Playing && NowPlayingSnapshot is { } snapshot)
        {
            ShowCard(snapshot, center);
        }
        else if (NowPlaying == NowPlayingState.Loading)
        {
            var reply = await _services.GetRequiredService<INowPlayingService>().GetAsync().ConfigureAwait(true);
            if (NowPlayingSanitizer.Sanitize(reply) is { } playing)
            {
                ShowCard(playing, center);
            }
        }
    }

    private void ShowCard(NowPlayingSnapshot snapshot, PixelPoint center)
    {
        var card = _card ??= new NowPlayingCard(_services);
        card.ShowAt(snapshot, center, _screens.ScreenFromPoint(center));
    }

    /// <summary>
    /// Synthesizes a key combination: waits until no modifier is physically down
    /// (every 15 ms, at most 100 tries), then 60 ms, key-down with the modifiers,
    /// key-up 40 ms later.
    /// </summary>
    private async Task PressAsync(KeyModifiers modifiers, int virtualKey)
    {
        for (var i = 0; i < 100 && (_hooks.IsKeyDown(VkControl) || _hooks.IsKeyDown(VkMenu) || _hooks.IsKeyDown(VkShift) || _hooks.IsKeyDown(VkLWin) || _hooks.IsKeyDown(VkRWin)); i++)
        {
            await Task.Delay(15).ConfigureAwait(true);
        }

        await Task.Delay(60).ConfigureAwait(true);
        var mods = new List<int>();
        if (modifiers.HasFlag(KeyModifiers.Control)) mods.Add(VkControl);
        if (modifiers.HasFlag(KeyModifiers.Alt)) mods.Add(VkMenu);
        if (modifiers.HasFlag(KeyModifiers.Shift)) mods.Add(VkShift);
        if (modifiers.HasFlag(KeyModifiers.Win)) mods.Add(VkLWin);
        var down = mods.Select(vk => (vk, KeyAction.Down)).Append((virtualKey, KeyAction.Down)).ToList();
        _hooks.SendKeys(down);
        await Task.Delay(40).ConfigureAwait(true);
        var up = new List<(int, KeyAction)> { (virtualKey, KeyAction.Up) };
        up.AddRange(mods.AsEnumerable().Reverse().Select(vk => (vk, KeyAction.Up)));
        _hooks.SendKeys(up);
    }

    // ── Now Playing ─────────────────────────────────────────────────────

    private static bool ContainsNowPlaying(IReadOnlyList<RadialItem> items) =>
        items.Any(i => (i.Kind == RadialItemKind.Media && i.Payload == RadialMediaIds.NowPlaying) || ContainsNowPlaying(i.Children));

    private void RefreshNowPlaying()
    {
        _nowPlayingCancel?.Cancel();
        var cancel = _nowPlayingCancel = new CancellationTokenSource(NowPlayingSanitizer.ReplyTimeout);
        NowPlaying = NowPlayingState.Loading;
        NowPlayingSnapshot = null;
        _ = Task.Run(async () =>
        {
            NowPlayingSnapshot? snapshot = null;
            try
            {
                snapshot = NowPlayingSanitizer.Sanitize(await _services.GetRequiredService<INowPlayingService>().GetAsync(cancel.Token).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                snapshot = null;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (cancel != _nowPlayingCancel)
                {
                    return;
                }

                NowPlayingSnapshot = snapshot;
                NowPlaying = snapshot is null ? NowPlayingState.NothingPlaying : NowPlayingState.Playing;
                _window?.Refresh();
            });
        });
    }

    public void Dispose()
    {
        Close();
        _triggerHook?.Dispose();
        _triggerHook = null;
        foreach (var observer in _observers)
        {
            observer.Dispose();
        }

        _nowPlayingCancel?.Cancel();
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Input;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Input;

/// <summary>
/// Settings › Quit &amp; close protection: two independently configured slots
/// (Alt+F4 with Ctrl+Q, Ctrl+W with Ctrl+F4), each with its confirmation
/// mode, app scope and feedback switch.
/// </summary>
public sealed class QuitProtectionSettingsPage : InputSettingsPage
{
    private readonly IAppCatalog? _catalog;

    public QuitProtectionSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _catalog = services.GetService<IAppCatalog>();
        Content = Stack(
            Header("quitProtection.name", "quitProtection.intro"),
            Note(L.Get("quitProtection.accessibilityCaption")),
            SlotCard(QuitSlot.Quit),
            SlotCard(QuitSlot.Close));
    }

    private Control SlotCard(QuitSlot slot)
    {
        var s = slot == QuitSlot.Quit ? QuitProtectionSlotSettings.Quit : QuitProtectionSlotSettings.Close;
        var chord = slot == QuitSlot.Quit ? QuitProtectionChord.AltF4 : QuitProtectionChord.CtrlW;
        var modes = new[]
        {
            (QuitProtectionModes.Hold, L.Get("quitProtection.hold")),
            (QuitProtectionModes.DoublePress, L.Get("quitProtection.doublePress")),
            (QuitProtectionModes.ExtraModifier, L.Get("quitProtection.extraModifier")),
        };

        // Extra modifiers that differ from the chord's own base (Alt+F4 cannot take Alt).
        var extras = new List<(string, string)> { (QuitProtectionModes.ShiftModifier, L.Get("quitProtection.shiftKey")) };
        if (!chord.Base.HasFlag(KeyModifiers.Alt)) extras.Add((QuitProtectionModes.OptionModifier, L.Get("quitProtection.optionKey")));
        if (!chord.Base.HasFlag(KeyModifiers.Control)) extras.Add((QuitProtectionModes.ControlModifier, L.Get("quitProtection.controlKey")));

        var extraCaption = IndentedNote(string.Empty);
        void UpdateExtraCaption()
        {
            var extra = QuitProtectionModes.EffectiveExtra(Settings.Get(s.ExtraModifier), chord.Base);
            var confirm = new KeyChord(chord.Base | extra, chord.VirtualKey);
            var text = L.Format("win.input.extraChordFormat", confirm.ToDisplayString());
            if (slot == QuitSlot.Close && extra == KeyModifiers.Shift)
            {
                text += " " + L.Get("win.input.extraConflictCtrlShiftW");
            }
            else if (extra == KeyModifiers.Shift && chord.Base == KeyModifiers.Alt)
            {
                text += " " + L.Get("win.input.extraConflictAltShift");
            }

            extraCaption.Text = text;
        }

        UpdateExtraCaption();
        Track(Settings.Observe(s.ExtraModifier.Key, () => Dispatcher.UIThread.Post(UpdateExtraCaption)));

        bool Mode(string mode) => Settings.Get(s.Enabled) && Settings.Get(s.Mode) == mode;
        var apps = new AppExclusionEditor(Settings, s.Exceptions, _catalog, null,
            titleKey: "quitProtection.exceptions", addKey: "quitProtection.addApp", emptyKey: "quitProtection.noExceptions", collapsible: false);

        return LiveCard(slot == QuitSlot.Quit ? "win.input.quitSlotTitle" : "win.input.closeSlotTitle",
            Toggle(s.Enabled, "Shield", "quitProtection.enabled", "quitProtection.enabledCaption"),
            ShowWhen(Toggle(s.SecondChord, "Keyboard",
                slot == QuitSlot.Quit ? "win.input.quitSecondChord" : "win.input.closeSecondChord",
                slot == QuitSlot.Quit ? "win.input.quitSecondChordCaption" : "win.input.closeSecondChordCaption"), s.Enabled),
            ShowWhen(Choice(s.Mode, "Timer", "quitProtection.mode", null, modes), s.Enabled),
            ShowWhen(MsSlider(s.HoldDurationMs, "HourglassHalf", "quitProtection.holdDuration", 250, 2000, 50), () => Mode(QuitProtectionModes.Hold), s.Enabled, s.Mode),
            ShowWhen(MsSlider(s.DoubleIntervalMs, "HourglassHalf", "quitProtection.doublePressInterval", 200, 1500, 50), () => Mode(QuitProtectionModes.DoublePress), s.Enabled, s.Mode),
            ShowWhen(Choice(s.ExtraModifier, "KeyboardShift", "quitProtection.modifier", null, extras), () => Mode(QuitProtectionModes.ExtraModifier), s.Enabled, s.Mode),
            ShowWhen(extraCaption, () => Mode(QuitProtectionModes.ExtraModifier), s.Enabled, s.Mode),
            ShowWhen(Choice(s.Scope, "AppsList", "quitProtection.appScope", null,
            [
                (QuitProtectionScopes.All, L.Get("quitProtection.allApps")),
                (QuitProtectionScopes.SelectedOnly, L.Get("quitProtection.selectedOnly")),
                (QuitProtectionScopes.AllExceptSelected, L.Get("quitProtection.allExceptSelected")),
            ]), s.Enabled),
            ShowWhen(apps, () => Settings.Get(s.Enabled) && Settings.Get(s.Scope) != QuitProtectionScopes.All, s.Enabled, s.Scope),
            ShowWhen(Toggle(s.ShowFeedback, "Eye", "quitProtection.feedback"), s.Enabled));
    }
}

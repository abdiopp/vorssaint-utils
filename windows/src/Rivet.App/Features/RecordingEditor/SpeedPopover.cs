// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Rivet.Core.Localization;
using Rivet.Core.RecordingEditor;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>
/// The export speed popover (spec 02 §3.30): presets, a custom stepper and
/// slider (0.25–4×, 0.01 steps), the resulting export duration, and Done to
/// write one undoable change. The preview always stays at 1×.
/// </summary>
internal static class SpeedPopover
{
    public static void Show(Control anchor, EditorSession session)
    {
        var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedRight };
        flyout.Content = Build(session, flyout.Hide);
        flyout.ShowAt(anchor);
    }

    /// <summary>The popover's content; <paramref name="close"/> dismisses it.</summary>
    public static Control Build(EditorSession session, Action close)
    {
        var draft = session.Document.ExportSpeed;
        var culture = CultureInfo.CurrentUICulture;
        var presetButtons = new List<(double Value, Button Button)>();
        var grid = new UniformGrid { Columns = 4 };
        var stepper = new NumericUpDown
        {
            Minimum = (decimal)ExportSpeed.Min,
            Maximum = (decimal)ExportSpeed.Max,
            Increment = 0.01m,
            FormatString = "0.00",
            Width = 120,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetName(stepper, L.Get("recorderExport.custom"));
        var slider = new Slider { Minimum = ExportSpeed.Min, Maximum = ExportSpeed.Max, SmallChange = 0.01, LargeChange = 0.25, TickFrequency = 0.01, IsSnapToTickEnabled = true };
        AutomationProperties.SetName(slider, L.Get("recorderExport.custom"));
        var duration = EditorUi.Label(string.Empty, 12, FontWeight.SemiBold);
        var syncing = false;

        void Sync()
        {
            syncing = true;
            stepper.Value = (decimal)draft;
            slider.Value = draft;
            duration.Text = RecordingNames.Elapsed(Math.Round(session.Timeline.OutputDuration / draft, MidpointRounding.AwayFromZero));
            foreach (var (value, button) in presetButtons)
            {
                button.FontWeight = Math.Abs(value - draft) < 0.001 ? FontWeight.Bold : FontWeight.Normal;
            }

            syncing = false;
        }

        foreach (var preset in ExportSpeed.Presets)
        {
            var value = preset;
            var button = new Button
            {
                Content = RecordingNames.Speed(value, culture),
                Classes = { "editor" },
                Margin = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            button.Click += (_, _) =>
            {
                draft = value;
                Sync();
            };
            presetButtons.Add((value, button));
            grid.Children.Add(button);
        }

        stepper.ValueChanged += (_, e) =>
        {
            if (!syncing && e.NewValue is { } v)
            {
                draft = ExportSpeed.Rounded((double)v);
                Sync();
            }
        };
        slider.ValueChanged += (_, e) =>
        {
            if (!syncing)
            {
                draft = ExportSpeed.Rounded(e.NewValue);
                Sync();
            }
        };

        var cancel = new Button { Content = L.Get("recorder.cancelButton"), Classes = { "editor" }, IsCancel = true, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        var done = new Button { Content = L.Get("screenshot.done"), Classes = { "editor", "primary" }, IsDefault = true, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => close();
        done.Click += (_, _) =>
        {
            session.SetExportSpeed(draft);
            close();
        };

        var customRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { EditorUi.Label(L.Get("recorderExport.custom"), 12.5), stepper } };
        Grid.SetColumn(stepper, 1);
        var range = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Children =
            {
                new TextBlock { Text = RecordingNames.Speed(ExportSpeed.Min, culture), Classes = { "caption" } },
                new TextBlock { Text = RecordingNames.Speed(ExportSpeed.Max, culture), Classes = { "caption" }, HorizontalAlignment = HorizontalAlignment.Right },
            },
        };
        Grid.SetColumn(range.Children[1], 1);
        var durationRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { EditorUi.Label(L.Get("recorderExport.duration"), 12.5), duration } };
        Grid.SetColumn(duration, 1);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, done } };

        var content = new StackPanel
        {
            Width = 320,
            Margin = new Thickness(6),
            Spacing = 12,
            Children =
            {
                EditorUi.Label(L.Get("recorderExport.speed"), 15, FontWeight.SemiBold),
                grid,
                customRow,
                new StackPanel { Spacing = 0, Children = { slider, range } },
                durationRow,
                EditorUi.Caption(L.Get("recorderExport.previewNote")),
                buttons,
            },
        };
        Sync();
        return content;
    }
}

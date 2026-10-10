// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.ScreenshotEditor;
using Rivet.Core.Shortcuts;
using Xunit;

namespace Rivet.Core.Tests.ScreenshotEditor;

public class WatermarkTests
{
    [Fact]
    public void Text_size_and_image_size_follow_the_formulas()
    {
        // short = 800: fontPx = max(8, round(800 · (0.02 + 0.14·0.3))) = round(49.6) = 50
        Assert.Equal(50, WatermarkGeometry.TextFontPixels(1200, 800, 0.3), 9);
        Assert.Equal(8, WatermarkGeometry.TextFontPixels(100, 100, 0), 9);
        // imageW = max(1, round(1200 · (0.05 + 0.45·0.3))) = round(222) = 222, imageH = 222 · 50/100
        Assert.Equal((222.0, 111.0), WatermarkGeometry.ImageSize(1200, 0.3, 100, 50));
    }

    [Fact]
    public void Placement_respects_the_margin_and_anchor()
    {
        // 1000 × 800, short = 800, no corners: margin = max(40, 0) = 40.
        var p = WatermarkGeometry.Place(1000, 800, 200, 50, WatermarkAnchor.BottomTrailing, 0, 0);
        Assert.Equal(40, p.Margin, 9);
        Assert.Equal(1, p.Fit, 9);
        Assert.Equal(1000 - 40 - 100, p.Center.X, 9);
        Assert.Equal(800 - 40 - 25, p.Center.Y, 9);

        var tl = WatermarkGeometry.Place(1000, 800, 200, 50, WatermarkAnchor.TopLeading, 0, 0);
        Assert.Equal(new ImgPoint(140, 65), tl.Center);
        var c = WatermarkGeometry.Place(1000, 800, 200, 50, WatermarkAnchor.Center, 0, 0);
        Assert.Equal(new ImgPoint(500, 400), c.Center);
    }

    [Fact]
    public void Rounded_corners_push_the_margin_out()
    {
        // R = 400 → clamp to short/2 = 400; inset = ceil(400 · (1 − √0.5)) + 1 = 119
        var p = WatermarkGeometry.Place(1000, 800, 10, 10, WatermarkAnchor.TopLeading, 0, 400);
        Assert.Equal(Math.Ceiling(400 * (1 - Math.Sqrt(0.5))) + 1, p.Margin, 9);
    }

    [Fact]
    public void Rotated_marks_use_their_rotated_bounds_and_shrink_to_fit()
    {
        var p = WatermarkGeometry.Place(400, 300, 1000, 100, WatermarkAnchor.Center, 90, 0);
        // rotated bounds = (100, 1000); avail = (400 − 30, 300 − 30) → fit = min(1, 370/100, 270/1000) = 0.27
        Assert.Equal(0.27, p.Fit, 9);
        Assert.Equal(Math.PI / 2, p.RotationRadians, 9);
    }

    [Fact]
    public void Sanitizing_caps_text_and_demotes_empty_marks()
    {
        var longText = new string('x', 200);
        var s = new WatermarkStyle { Kind = WatermarkKind.Text, Text = "  " + longText }.Sanitized();
        Assert.Equal(120, s.Text.Length);
        Assert.Equal(WatermarkKind.None, new WatermarkStyle { Kind = WatermarkKind.Text, Text = "   " }.Sanitized().Kind);
        Assert.Equal(WatermarkKind.None, new WatermarkStyle { Kind = WatermarkKind.Image }.Sanitized().Kind);
        var clamped = new WatermarkStyle { Opacity = 0, Size = 4, Rotation = -500 }.Sanitized();
        Assert.Equal((0.05, 1.0, -90.0), (clamped.Opacity, clamped.Size, clamped.Rotation));
        Assert.Equal(0.4, new WatermarkStyle { Opacity = double.NaN }.Sanitized().Opacity);
    }

    [Fact]
    public void Json_round_trips_with_mac_field_names()
    {
        var style = new WatermarkStyle
        {
            Kind = WatermarkKind.Text, Text = "Draft", Color = AnnotationColor.Blue, Anchor = WatermarkAnchor.Top,
            Size = 0.5, Opacity = 0.7, Rotation = 30,
        };
        var json = WatermarkCodec.Encode(style);
        Assert.Contains("\"anchor\":\"top\"", json, StringComparison.Ordinal);
        Assert.Contains("\"color\":\"blue\"", json, StringComparison.Ordinal);
        Assert.Equal(style, WatermarkCodec.Decode(json));
        Assert.Equal(WatermarkStyle.None, WatermarkCodec.Decode("{not json"));
        Assert.Equal(AnnotationColor.White, WatermarkCodec.Decode("{\"kind\":\"text\",\"text\":\"a\",\"color\":\"mauve\"}").Color);
    }

    [Fact]
    public void Presets_drop_none_and_keep_the_last_twelve()
    {
        var presets = Enumerable.Range(0, 15).Select(i => new WatermarkStyle { Kind = WatermarkKind.Text, Text = $"m{i}" })
            .Append(WatermarkStyle.None);
        var decoded = WatermarkCodec.DecodePresets(WatermarkCodec.EncodePresets(presets));
        Assert.Equal(12, decoded.Count);
        Assert.Equal("m3", decoded[0].Text);
        Assert.Equal("m14", decoded[^1].Text);
    }
}

public class ToolShortcutTests
{
    [Fact]
    public void Order_parsing_drops_bad_ids_and_appends_missing_tools()
    {
        var order = ToolOrder.Parse("crop,bogus,arrow,crop,select");
        Assert.Equal(EditorTool.Crop, order[0]);
        Assert.Equal(EditorTool.Arrow, order[1]);
        Assert.Equal(EditorTool.Select, order[2]);
        Assert.Equal(13, order.Count);
        Assert.Equal(13, order.Distinct().Count());
        Assert.Equal("select,arrow,pixelate,crop,text,sticker,rect,highlight,freehand,line,ellipse,counter,redact", ToolOrder.DefaultCsv);
    }

    [Fact]
    public void Default_order_gives_the_first_nine_tools_digits()
    {
        var order = ToolOrder.Default;
        Assert.Equal(1, ToolOrder.DigitOf(order, EditorTool.Select));
        Assert.Equal(3, ToolOrder.DigitOf(order, EditorTool.Pixelate));
        Assert.Equal(9, ToolOrder.DigitOf(order, EditorTool.Freehand));
        Assert.Null(ToolOrder.DigitOf(order, EditorTool.Line));
    }

    [Fact]
    public void Bindings_drop_reserved_and_duplicate_keys_keeping_the_canonical_owner()
    {
        var csv = "redact=:0x52,rect=:0x52,text=ctrl:0x43,arrow=:0x41,bogus=:0x42,line=nonsense";
        var bindings = ToolBindings.Parse(csv);
        Assert.Equal(KeyChord.Of(KeyModifiers.None, 0x52), bindings.KeyFor(EditorTool.Rect));
        Assert.Null(bindings.KeyFor(EditorTool.Redact));
        Assert.Null(bindings.KeyFor(EditorTool.Text));        // Ctrl+C belongs to the editor
        Assert.Equal(KeyChord.Of(KeyModifiers.None, 0x41), bindings.KeyFor(EditorTool.Arrow));
        Assert.Equal("arrow=:0x41,rect=:0x52", bindings.Format());
    }

    [Fact]
    public void Reserved_editor_keys()
    {
        Assert.True(EditorReservedKeys.IsReserved(KeyChord.Of(KeyModifiers.Control | KeyModifiers.Shift, VirtualKeys.Letter('S'))));
        Assert.True(EditorReservedKeys.IsReserved(KeyChord.Of(KeyModifiers.None, VirtualKeys.Escape)));
        Assert.True(EditorReservedKeys.IsReserved(KeyChord.Of(KeyModifiers.Shift, VirtualKeys.Return)));
        Assert.True(EditorReservedKeys.IsReserved(KeyChord.Of(KeyModifiers.Control, VirtualKeys.Letter('Y'))));
        Assert.False(EditorReservedKeys.IsReserved(KeyChord.Of(KeyModifiers.None, VirtualKeys.Letter('A'))));
        Assert.False(EditorReservedKeys.IsReserved(KeyChord.Of(KeyModifiers.Control, VirtualKeys.Letter('A'))));
    }

    [Fact]
    public void Recording_a_digit_moves_the_tool_into_that_slot_and_clears_its_key()
    {
        var bindings = ToolBindings.Empty.With(EditorTool.Line, KeyChord.Of(KeyModifiers.None, 0x4C));
        var outcome = ToolKeyRecorder.Record(ToolOrder.Default, bindings, EditorTool.Line, KeyChord.Of(KeyModifiers.None, VirtualKeys.Digit(2)), typedDigit: 2);
        Assert.True(outcome.Accepted);
        Assert.Equal(EditorTool.Line, outcome.Order[1]);
        Assert.Null(outcome.Bindings.KeyFor(EditorTool.Line));
    }

    [Fact]
    public void Delete_clears_the_key_and_moves_the_tool_just_below_slot_nine()
    {
        var bindings = ToolBindings.Empty.With(EditorTool.Select, KeyChord.Of(KeyModifiers.None, 0x56));
        var outcome = ToolKeyRecorder.Record(ToolOrder.Default, bindings, EditorTool.Select, KeyChord.None, typedDigit: null);
        Assert.Equal(EditorTool.Select, outcome.Order[9]);
        Assert.Null(outcome.Bindings.KeyFor(EditorTool.Select));
        Assert.Null(ToolOrder.DigitOf(outcome.Order, EditorTool.Select));
    }

    [Fact]
    public void Rejected_keys_leave_the_previous_binding_untouched()
    {
        var bindings = ToolBindings.Empty.With(EditorTool.Arrow, KeyChord.Of(KeyModifiers.None, 0x41));
        var reserved = ToolKeyRecorder.Record(ToolOrder.Default, bindings, EditorTool.Rect, KeyChord.Of(KeyModifiers.Control, VirtualKeys.Letter('Z')), null);
        Assert.Equal(ToolKeyRejection.ReservedByEditor, reserved.Rejection);
        Assert.Same(bindings, reserved.Bindings);

        var taken = ToolKeyRecorder.Record(ToolOrder.Default, bindings, EditorTool.Rect, KeyChord.Of(KeyModifiers.None, 0x41), null, toolName: t => t.ToString());
        Assert.Equal(ToolKeyRejection.UsedByTool, taken.Rejection);
        Assert.Equal("Arrow", taken.ConflictName);

        var global = ToolKeyRecorder.Record(ToolOrder.Default, bindings, EditorTool.Rect, KeyChord.Of(KeyModifiers.Alt, 0x52), null, globalShortcutOwner: _ => "Screenshot");
        Assert.Equal(ToolKeyRejection.UsedByShortcut, global.Rejection);

        var ok = ToolKeyRecorder.Record(ToolOrder.Default, bindings, EditorTool.Rect, KeyChord.Of(KeyModifiers.None, 0x52), null);
        Assert.True(ok.Accepted);
        Assert.Equal(EditorTool.Rect, ok.Bindings.ToolFor(KeyChord.Of(KeyModifiers.None, 0x52)));
    }

    [Fact]
    public void Key_presses_resolve_custom_keys_then_digits()
    {
        var bindings = ToolBindings.Empty.With(EditorTool.Rect, KeyChord.Of(KeyModifiers.None, 0x52))
            .With(EditorTool.Arrow, KeyChord.Of(KeyModifiers.Shift, 0x41));
        var order = ToolOrder.Default;
        Assert.Equal(EditorTool.Rect, ToolKeyMap.Resolve(order, bindings, true, KeyChord.Of(KeyModifiers.None, 0x52), null));
        Assert.Null(ToolKeyMap.Resolve(order, bindings, true, KeyChord.Of(KeyModifiers.None, 0x41), null));   // exact modifier match
        Assert.Equal(EditorTool.Pixelate, ToolKeyMap.Resolve(order, bindings, true, KeyChord.Of(KeyModifiers.None, VirtualKeys.Digit(3)), 3));
        // Slot 2 is Arrow, which has a custom key, so the digit does nothing.
        Assert.Null(ToolKeyMap.Resolve(order, bindings, true, KeyChord.Of(KeyModifiers.None, VirtualKeys.Digit(2)), 2));
        Assert.Null(ToolKeyMap.Resolve(order, bindings, false, KeyChord.Of(KeyModifiers.None, 0x52), null));
        Assert.Null(ToolKeyMap.Resolve(order, bindings, true, KeyChord.Of(KeyModifiers.Control, VirtualKeys.Digit(3)), 3));
    }

    [Fact]
    public void A_custom_key_that_types_a_digit_is_suspended_not_erased()
    {
        // On AZERTY, Shift+& types 1: a custom key Shift+VK_1 is suspended and slot 1 keeps working.
        var shiftOne = KeyChord.Of(KeyModifiers.Shift, VirtualKeys.Digit(1));
        var bindings = ToolBindings.Empty.With(EditorTool.Text, shiftOne);
        int? Azerty(KeyChord c) => c == shiftOne ? 1 : null;
        Assert.Equal(EditorTool.Select, ToolKeyMap.Resolve(ToolOrder.Default, bindings, true, shiftOne, 1, Azerty));
        Assert.Equal("5", ToolKeyMap.Badge(ToolOrder.Default, bindings, EditorTool.Text, true, c => c.ToDisplayString(), Azerty));
        Assert.Equal("Shift+1", ToolKeyMap.Badge(ToolOrder.Default, bindings, EditorTool.Text, true, c => c.ToDisplayString()));
        Assert.NotNull(bindings.KeyFor(EditorTool.Text));
    }

    [Fact]
    public void Us_layout_digits()
    {
        var layout = new UsKeyboardLayoutInfo();
        Assert.Equal(4, layout.DigitTypedBy(KeyChord.Of(KeyModifiers.None, VirtualKeys.Digit(4))));
        Assert.Null(layout.DigitTypedBy(KeyChord.Of(KeyModifiers.Shift, VirtualKeys.Digit(4))));
        Assert.Equal(7, layout.DigitTypedBy(KeyChord.Of(KeyModifiers.None, VirtualKeys.NumPad0 + 7)));
    }
}

public class EditorFilesTests
{
    [Fact]
    public void Default_name_is_dated_and_colon_free()
    {
        var name = EditorFiles.DefaultName(new DateTime(2026, 10, 9, 14, 5, 33), "Screenshot");
        Assert.Equal("Screenshot 2026-10-09 at 14.05.33.png", name);
    }

    [Fact]
    public void Names_are_made_safe_for_windows()
    {
        Assert.Equal("a-b-c-d", EditorFiles.Sanitize("a/b\\c:d"));
        Assert.Equal("CON_", EditorFiles.Sanitize("CON"));
        Assert.Equal("x", EditorFiles.Sanitize("x. . "));
    }

    [Fact]
    public void Unique_paths_count_up()
    {
        var taken = new HashSet<string> { Path.Combine("f", "n.png"), Path.Combine("f", "n 2.png") };
        Assert.Equal(Path.Combine("f", "n 3.png"), EditorFiles.UniquePath("f", "n.png", taken.Contains));
        Assert.True(EditorFiles.IsDragFolderName("ScreenshotDrag-" + Guid.NewGuid()));
        Assert.False(EditorFiles.IsDragFolderName("ScreenshotDrag-nope"));
    }

    [Fact]
    public void Qr_payloads_join_in_reading_order_and_only_single_http_links_open()
    {
        var codes = new[]
        {
            ("second", new ImgRect(300, 400, 50, 50)),
            ("first", new ImgRect(10, 10, 50, 50)),
            ("beside", new ImgRect(200, 405, 50, 50)),
            ("   ", new ImgRect(0, 0, 5, 5)),
        };
        Assert.Equal("first\nbeside\nsecond", EditorFiles.JoinQrPayloads(codes, 1000));
        Assert.Equal("https://example.com/a?b=1", EditorFiles.OpenableUrl(" https://example.com/a?b=1 ")!.ToString());
        Assert.Null(EditorFiles.OpenableUrl("mailto:someone@example.com"));
        Assert.Null(EditorFiles.OpenableUrl("https://exa mple.com"));
        Assert.Null(EditorFiles.OpenableUrl("ftp://example.com"));
    }

    [Fact]
    public void Clipboard_scale_inference()
    {
        Assert.Equal(2, EditorFiles.InferScale(200, 100, 100, 50), 9);
        Assert.Equal(1, EditorFiles.InferScale(200, 100, 100, 100), 9);   // disagree
        Assert.Equal(1, EditorFiles.InferScale(1000, 1000, 100, 100), 9); // 10× is outside 0.5…4
        Assert.Equal(1, EditorFiles.InferScale(0, 1, 1, 1), 9);
        Assert.Equal("@1.5x", EditorFiles.ScaleLabel(1.5));
        Assert.Equal("@2x", EditorFiles.ScaleLabel(2));
        Assert.Null(EditorFiles.ScaleLabel(1));
    }
}

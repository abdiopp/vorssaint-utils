// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.ScreenshotEditor;

/// <summary>The 13 editor tools, in canonical order (which is also the default rail order).</summary>
public enum EditorTool
{
    Select,
    Arrow,
    Pixelate,
    Crop,
    Text,
    Sticker,
    Rect,
    Highlight,
    Freehand,
    Line,
    Ellipse,
    Counter,
    Redact,
}

public static class EditorTools
{
    /// <summary>Canonical order: select, arrow, pixelate, crop, text, sticker, rect, highlight, freehand, line, ellipse, counter, redact.</summary>
    public static IReadOnlyList<EditorTool> Canonical { get; } = Enum.GetValues<EditorTool>();

    /// <summary>The ids stored in settings (identical to the macOS app).</summary>
    public static string Id(EditorTool tool) => tool switch
    {
        EditorTool.Select => "select",
        EditorTool.Arrow => "arrow",
        EditorTool.Pixelate => "pixelate",
        EditorTool.Crop => "crop",
        EditorTool.Text => "text",
        EditorTool.Sticker => "sticker",
        EditorTool.Rect => "rect",
        EditorTool.Highlight => "highlight",
        EditorTool.Freehand => "freehand",
        EditorTool.Line => "line",
        EditorTool.Ellipse => "ellipse",
        EditorTool.Counter => "counter",
        _ => "redact",
    };

    public static bool TryParse(string? id, out EditorTool tool)
    {
        foreach (var candidate in Canonical)
        {
            if (Id(candidate) == id)
            {
                tool = candidate;
                return true;
            }
        }

        tool = EditorTool.Select;
        return false;
    }

    public static string TitleKey(EditorTool tool) => tool switch
    {
        EditorTool.Select => "screenshot.toolSelect",
        EditorTool.Arrow => "screenshot.toolArrow",
        EditorTool.Pixelate => "screenshot.toolBlur",
        EditorTool.Crop => "screenshot.toolCrop",
        EditorTool.Text => "screenshot.toolText",
        EditorTool.Sticker => "screenshot.toolSticker",
        EditorTool.Rect => "screenshot.toolRect",
        EditorTool.Highlight => "screenshot.toolHighlight",
        EditorTool.Freehand => "screenshot.toolFreehand",
        EditorTool.Line => "screenshot.toolLine",
        EditorTool.Ellipse => "screenshot.toolEllipse",
        EditorTool.Counter => "screenshot.toolCounter",
        _ => "screenshot.toolRedact",
    };

    /// <summary>Fluent UI System Icons name for the rail button.</summary>
    public static string Icon(EditorTool tool) => tool switch
    {
        EditorTool.Select => "Cursor",
        EditorTool.Arrow => "ArrowUpRight",
        EditorTool.Pixelate => "Blur",
        EditorTool.Crop => "Crop",
        EditorTool.Text => "TextFont",
        EditorTool.Sticker => "Emoji",
        EditorTool.Rect => "RectangleLandscape",
        EditorTool.Highlight => "Highlight",
        EditorTool.Freehand => "Pen",
        EditorTool.Line => "Line",
        EditorTool.Ellipse => "Circle",
        EditorTool.Counter => "NumberCircle1",
        _ => "Square",
    };

    /// <summary>Tools that place marks (everything except Select and Crop).</summary>
    public static bool IsCreation(EditorTool tool) => tool is not (EditorTool.Select or EditorTool.Crop);

    /// <summary>Tools that place a mark with a single tap.</summary>
    public static bool IsTapTool(EditorTool tool) => tool is EditorTool.Text or EditorTool.Sticker or EditorTool.Counter;

    /// <summary>Tools that draw a mark by dragging.</summary>
    public static bool IsDragTool(EditorTool tool) => IsCreation(tool) && !IsTapTool(tool);

    /// <summary>The annotation kind a creation tool makes.</summary>
    public static AnnotationKind KindFor(EditorTool tool) => tool switch
    {
        EditorTool.Arrow => AnnotationKind.Arrow,
        EditorTool.Pixelate => AnnotationKind.Blur,
        EditorTool.Text => AnnotationKind.Text,
        EditorTool.Sticker => AnnotationKind.Sticker,
        EditorTool.Rect => AnnotationKind.Rect,
        EditorTool.Highlight => AnnotationKind.Highlight,
        EditorTool.Freehand => AnnotationKind.Freehand,
        EditorTool.Line => AnnotationKind.Line,
        EditorTool.Ellipse => AnnotationKind.Ellipse,
        EditorTool.Counter => AnnotationKind.Counter,
        EditorTool.Redact => AnnotationKind.Redact,
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Not a creation tool."),
    };

    /// <summary>The tool a kind is drawn with (for syncing the style bar).</summary>
    public static EditorTool ToolFor(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Arrow => EditorTool.Arrow,
        AnnotationKind.Line => EditorTool.Line,
        AnnotationKind.Rect => EditorTool.Rect,
        AnnotationKind.Ellipse => EditorTool.Ellipse,
        AnnotationKind.Freehand => EditorTool.Freehand,
        AnnotationKind.Highlight => EditorTool.Highlight,
        AnnotationKind.Redact => EditorTool.Redact,
        AnnotationKind.Blur => EditorTool.Pixelate,
        AnnotationKind.Text => EditorTool.Text,
        AnnotationKind.Sticker => EditorTool.Sticker,
        _ => EditorTool.Counter,
    };
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules.RadialMenu;
using Inline = Avalonia.Controls.Documents.Inline;

namespace Rivet.App.Features.Scratchpad;

/// <summary>
/// The read-only Markdown preview (spec 07 §3.3.8): CommonMark plus GFM
/// strikethrough, rendered to selectable Avalonia text. Headings are semibold
/// at body + 5/+3/+1, list items get "• " or "N. " prefixes indented two
/// spaces per level, quotes a "▏ " prefix, code blocks and inline code are
/// monospaced on a 7 % background, a thematic break is 24 × "─", images show
/// their alt text, and links (http, https and mailto) open on click. A parse
/// failure shows the raw text as one paragraph.
/// </summary>
public static class MarkdownPreview
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UsePipeTables()
        .Build();

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, monospace");

    public sealed record Style(double FontSize, IBrush Foreground, IBrush Secondary, IBrush Accent, Action<string>? OpenLink);

    public static Control Render(string markdown, Style style)
    {
        var panel = new StackPanel { Spacing = 0 };
        try
        {
            var document = Markdown.Parse(markdown, Pipeline);
            var first = true;
            foreach (var block in document)
            {
                AddBlock(panel, block, style, depth: 0, prefix: null, ref first, tight: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("scratchpad", "Markdown preview failed; showing the plain text.", ex);
            panel.Children.Clear();
            panel.Children.Add(Paragraph(style, [new Run(markdown)], []));
        }

        return panel;
    }

    private static void AddBlock(StackPanel panel, Block block, Style style, int depth, string? prefix, ref bool first, bool tight)
    {
        // Blocks are separated by a blank line; items of one list or quote by a single newline.
        var spacing = first ? 0 : tight ? 2 : style.FontSize * 0.9;
        first = false;
        switch (block)
        {
            case HeadingBlock heading:
            {
                var size = style.FontSize + heading.Level switch { 1 => 5, 2 => 3, _ => 1 };
                var (inlines, links) = Inlines(heading.Inline, style, size);
                var text = Paragraph(style, inlines, links, size, FontWeight.SemiBold);
                text.Margin = new Thickness(0, spacing, 0, 0);
                panel.Children.Add(text);
                break;
            }

            case ParagraphBlock paragraph:
            {
                var (inlines, links) = Inlines(paragraph.Inline, style, style.FontSize);
                if (prefix is not null)
                {
                    inlines.Insert(0, new Run(prefix) { Foreground = style.Secondary });
                    links = links.Select(l => l with { Start = l.Start + prefix.Length }).ToList();
                }

                var text = Paragraph(style, inlines, links);
                text.Margin = new Thickness(depth * style.FontSize, spacing, 0, 0);
                panel.Children.Add(text);
                break;
            }

            case ListBlock list:
            {
                var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                var firstItem = true;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var marker = list.IsOrdered ? $"{number++}. " : "• ";
                    var firstInner = true;
                    foreach (var inner in item)
                    {
                        var nested = inner is ListBlock;
                        AddBlock(panel, inner, style, depth + (nested ? 1 : 0), firstInner && !nested ? marker : null, ref first, tight: !firstItem || !firstInner);
                        firstInner = false;
                    }

                    firstItem = false;
                }

                break;
            }

            case QuoteBlock quote:
            {
                var firstInQuote = true;
                foreach (var inner in quote)
                {
                    AddBlock(panel, inner, style with { Foreground = style.Secondary }, depth, "▏ ", ref first, tight: !firstInQuote);
                    firstInQuote = false;
                }

                break;
            }

            case CodeBlock code:
            {
                var text = code.Lines.ToString().TrimEnd('\n', '\r');
                var block1 = new SelectableTextBlock
                {
                    Text = text,
                    FontFamily = Mono,
                    FontSize = Math.Max(9, style.FontSize - 1),
                    Foreground = style.Foreground,
                    TextWrapping = TextWrapping.Wrap,
                };
                panel.Children.Add(new Border
                {
                    Background = Tint(style.Foreground, 0.07),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 6),
                    Margin = new Thickness(depth * style.FontSize, spacing, 0, 0),
                    Child = block1,
                });
                break;
            }

            case ThematicBreakBlock:
                panel.Children.Add(new TextBlock { Text = new string('─', 24), Foreground = style.Secondary, FontSize = style.FontSize, Margin = new Thickness(0, spacing, 0, 0) });
                break;

            case Table table:
                // Tables are parsed but not laid out: each cell becomes its own paragraph.
                foreach (var row in table.OfType<TableRow>())
                {
                    foreach (var cell in row.OfType<TableCell>())
                    {
                        foreach (var inner in cell)
                        {
                            AddBlock(panel, inner, style, depth, null, ref first, tight: true);
                        }
                    }
                }

                break;

            case HtmlBlock html:
                panel.Children.Add(Paragraph(style, [new Run(html.Lines.ToString())], []));
                break;

            case ContainerBlock container:
                foreach (var inner in container)
                {
                    AddBlock(panel, inner, style, depth, prefix, ref first, tight);
                }

                break;
        }
    }

    public readonly record struct LinkRange(int Start, int Length, string Url);

    private static SelectableTextBlock Paragraph(Style style, List<Inline> inlines, List<LinkRange> links, double? size = null, FontWeight? weight = null)
    {
        var block = new SelectableTextBlock
        {
            FontSize = size ?? style.FontSize,
            FontWeight = weight ?? FontWeight.Normal,
            Foreground = style.Foreground,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = (size ?? style.FontSize) * 1.35,
        };
        block.Inlines ??= [];
        foreach (var inline in inlines)
        {
            block.Inlines.Add(inline);
        }

        if (links.Count > 0)
        {
            block.Tag = links;
            block.Cursor = new Cursor(StandardCursorType.Arrow);
            block.PointerMoved += (_, e) =>
            {
                var position = Hit(block, e.GetPosition(block));
                block.Cursor = new Cursor(links.Any(l => position >= l.Start && position < l.Start + l.Length) ? StandardCursorType.Hand : StandardCursorType.Ibeam);
            };
            block.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left || block.SelectionStart != block.SelectionEnd)
                {
                    return;
                }

                var position = Hit(block, e.GetPosition(block));
                var link = links.FirstOrDefault(l => position >= l.Start && position < l.Start + l.Length);
                if (link.Url is { Length: > 0 } url && RadialLinks.IsWebOrMail(url))
                {
                    style.OpenLink?.Invoke(url);
                }
            };
        }

        return block;
    }

    private static int Hit(SelectableTextBlock block, Point point)
    {
        try
        {
            var hit = block.TextLayout.HitTestPoint(point);
            return hit.IsInside ? hit.TextPosition : -1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return -1;
        }
    }

    private static (List<Inline> Inlines, List<LinkRange> Links) Inlines(ContainerInline? container, Style style, double size)
    {
        var result = new List<Inline>();
        var links = new List<LinkRange>();
        var offset = 0;
        if (container is not null)
        {
            Walk(container, result, links, ref offset, style, size, new Formatting());
        }

        return (result, links);
    }

    private readonly record struct Formatting(bool Bold = false, bool Italic = false, bool Strike = false, string? Link = null);

    private static void Walk(ContainerInline container, List<Inline> output, List<LinkRange> links, ref int offset, Style style, double size, Formatting format)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Add(output, links, ref offset, literal.Content.ToString(), style, size, format);
                    break;
                case EmphasisInline emphasis:
                    var next = emphasis.DelimiterChar == '~'
                        ? format with { Strike = true }
                        : emphasis.DelimiterCount >= 2 ? format with { Bold = true } : format with { Italic = true };
                    Walk(emphasis, output, links, ref offset, style, size, next);
                    break;
                case CodeInline code:
                    var run = new Run(code.Content)
                    {
                        FontFamily = Mono,
                        FontSize = Math.Max(9, size - 1),
                        Background = Tint(style.Foreground, 0.07),
                    };
                    output.Add(run);
                    offset += code.Content.Length;
                    break;
                case LinkInline link when link.IsImage:
                    // Images show their alt text.
                    Walk(link, output, links, ref offset, style, size, format);
                    break;
                case LinkInline link:
                    Walk(link, output, links, ref offset, style, size, format with { Link = link.Url ?? string.Empty });
                    break;
                case AutolinkInline auto:
                    Add(output, links, ref offset, auto.Url, style, size, format with { Link = auto.IsEmail ? "mailto:" + auto.Url : auto.Url });
                    break;
                case LineBreakInline lineBreak:
                    if (lineBreak.IsHard)
                    {
                        output.Add(new LineBreak());
                        offset += 1;
                    }
                    else
                    {
                        // A soft break inside a paragraph renders as a space.
                        Add(output, links, ref offset, " ", style, size, format);
                    }

                    break;
                case HtmlEntityInline entity:
                    Add(output, links, ref offset, entity.Transcoded.ToString(), style, size, format);
                    break;
                case HtmlInline html:
                    Add(output, links, ref offset, html.Tag, style, size, format);
                    break;
                case ContainerInline nested:
                    Walk(nested, output, links, ref offset, style, size, format);
                    break;
                case LeafInline leaf:
                    var text = leaf.ToString() ?? string.Empty;
                    Add(output, links, ref offset, text, style, size, format);
                    break;
            }
        }
    }

    private static void Add(List<Inline> output, List<LinkRange> links, ref int offset, string text, Style style, double size, Formatting format)
    {
        if (text.Length == 0)
        {
            return;
        }

        var run = new Run(text);
        if (format.Bold)
        {
            run.FontWeight = FontWeight.SemiBold;
        }

        if (format.Italic)
        {
            run.FontStyle = FontStyle.Italic;
        }

        var decorations = new TextDecorationCollection();
        if (format.Strike)
        {
            decorations.AddRange(TextDecorations.Strikethrough);
        }

        if (format.Link is not null)
        {
            run.Foreground = style.Accent;
            decorations.AddRange(TextDecorations.Underline);
            links.Add(new LinkRange(offset, text.Length, format.Link));
        }

        if (decorations.Count > 0)
        {
            run.TextDecorations = decorations;
        }

        output.Add(run);
        offset += text.Length;
    }

    private static IBrush Tint(IBrush brush, double opacity) =>
        brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, opacity) : new SolidColorBrush(Colors.Gray, opacity);

    /// <summary>Plain text of the rendered preview (tests and accessibility).</summary>
    public static string PlainText(Control rendered)
    {
        var builder = new StringBuilder();
        void Visit(Control control)
        {
            switch (control)
            {
                case TextBlock block:
                    builder.AppendLine(block.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.Select(i => i is Run r ? r.Text : i is LineBreak ? "\n" : string.Empty)) : block.Text);
                    break;
                case Panel panel:
                    foreach (var child in panel.Children)
                    {
                        Visit(child);
                    }

                    break;
                case Decorator decorator when decorator.Child is { } child:
                    Visit(child);
                    break;
            }
        }

        Visit(rendered);
        return builder.ToString();
    }
}

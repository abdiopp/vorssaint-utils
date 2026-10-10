// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Rivet.App.Controls;
using Rivet.Core.Modules.RadialMenu;
using Xunit;

namespace Rivet.App.Tests.RadialMenu;

/// <summary>
/// Icon names written as strings in the everyday-tools modules (settings rows,
/// actions, tiles) and the radial menu's automatic slice symbols must be real
/// Fluent icons; an unknown name would silently show the neutral fallback glyph.
/// </summary>
public partial class EverydayIconTests
{
    private static readonly string[] Modules = ["Shelf", "RadialMenu", "Scratchpad", "CleaningMode", "CameraPreview", "MediaTools"];

    [Fact]
    public void Icon_names_in_the_module_sources_exist()
    {
        var root = FindRepoRoot();
        var unknown = new List<string>();
        foreach (var module in Modules)
        {
            var folder = Path.Combine(root, "src", "Rivet.App", "Features", module);
            foreach (var file in Directory.EnumerateFiles(folder, "*.cs"))
            {
                var text = File.ReadAllText(file);
                foreach (var regex in new[] { RowIcon(), PropertyIcon(), ActionButtonIcon() })
                {
                    foreach (Match match in regex.Matches(text))
                    {
                        var name = match.Groups["icon"].Value;
                        if (!IconConverter.IsKnown(name))
                        {
                            unknown.Add($"{module}/{Path.GetFileName(file)}: {name}");
                        }
                    }
                }
            }
        }

        Assert.True(unknown.Count == 0, "Unknown icon names:\n" + string.Join('\n', unknown.Distinct()));
    }

    [Fact]
    public void Automatic_slice_symbols_exist()
    {
        var items = new List<RadialItem>
        {
            new() { Kind = RadialItemKind.App, Payload = "C:\\app.exe" },
            new() { Kind = RadialItemKind.File, Payload = "~/Downloads" },
            new() { Kind = RadialItemKind.File, Payload = "C:\\notes.txt" },
            new() { Kind = RadialItemKind.Url, Payload = "https://example.org" },
            new() { Kind = RadialItemKind.Shortcut, Payload = "ctrl:0x41" },
            new() { Kind = RadialItemKind.Tool, Payload = "screenshot" },
            new() { Kind = RadialItemKind.Submenu },
        };
        items.AddRange(RadialQuickToggleIds.All.Select(id => new RadialItem { Kind = RadialItemKind.QuickToggle, Payload = id }));
        items.AddRange(WindowLayoutActions.All.Select(id => new RadialItem { Kind = RadialItemKind.WindowLayout, Payload = id }));
        items.AddRange(RadialMediaIds.All.Select(id => new RadialItem { Kind = RadialItemKind.Media, Payload = id }));
        var unknown = items.Select(i => RadialLabels.AutomaticSymbol(i)).Where(n => !IconConverter.IsKnown(n)).Distinct().ToList();
        Assert.True(unknown.Count == 0, "Unknown slice symbols: " + string.Join(", ", unknown));
        Assert.True(IconConverter.IsKnown("CircleMultipleSubtractCheckmark"));
        Assert.True(IconConverter.IsKnown("MusicNote2"));
    }

    // Row("Icon", …), Toggle(setting, "Icon", …), Choice(setting, "Icon", …), Slider(setting, "Icon", …)
    [GeneratedRegex(""""(?:\bRow\(\s*|\b(?:Toggle|Choice|Slider|EndLabelledSlider)\(\s*[A-Za-z0-9_.\[\]]+,\s*)"(?<icon>[A-Z][A-Za-z0-9]+)"""")]
    private static partial Regex RowIcon();

    // Icon = "Icon"
    [GeneratedRegex(""""\bIcon\s*=\s*"(?<icon>[A-Z][A-Za-z0-9]+)"""")]
    private static partial Regex PropertyIcon();

    // ActionButton(text, onClick, "Icon"…)
    [GeneratedRegex("""\bActionButton\([^;]*?,\s*"(?<icon>[A-Z][A-Za-z0-9]+)"(?:,\s*accent:\s*true)?\)""")]
    private static partial Regex ActionButtonIcon();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rivet.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

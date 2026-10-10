// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Contracts;

/// <summary>Puts things on the shelf from other features (e.g. "Add to Shelf" in the screenshot editor).</summary>
public interface IShelfIntake
{
    bool IsAvailable { get; }

    void AddFiles(IReadOnlyList<string> paths);

    void AddText(string text);
}

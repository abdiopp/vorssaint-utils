// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Imaging.Tests;

/// <summary>Writes rendered images for human review to tests/artifacts/snapshots (or $RIVET_SNAPSHOTS).</summary>
internal static class Snapshots
{
    public static string Directory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("RIVET_SNAPSHOTS");
            var dir = overridden ?? Path.Combine(FindRepoRoot(), "tests", "artifacts", "snapshots");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Save(SKImage image, string name)
    {
        var path = Path.Combine(Directory, name + ".png");
        SkiaConvert.SavePng(image, path);
        return path;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rivet.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Path.GetTempPath();
    }
}

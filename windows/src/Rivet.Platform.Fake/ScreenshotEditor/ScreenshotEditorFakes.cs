// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;
using Rivet.Core.Platform;
using Rivet.Core.ScreenshotEditor;

namespace Rivet.Platform.Fake.ScreenshotEditor;

/// <summary>Development builds have no OCR engine: text-only blurs cover their whole area and no words are selectable.</summary>
public sealed class FakeTextRecognizer : ITextRecognizer
{
    public bool IsAvailable => false;

    public int? MaxImageDimension => null;

    public Task<OcrPage?> RecognizeAsync(PixelBuffer image, CancellationToken cancellationToken) => Task.FromResult<OcrPage?>(null);
}

/// <summary>The share sheet exists only on Windows.</summary>
public sealed class FakeShareSheet : IShareSheet
{
    public bool IsAvailable => false;

    public Task<bool> ShareFileAsync(nint windowHandle, string filePath, string title, Action targetChosen)
    {
        Log.Info("screenshotEditor", $"[fake] share {filePath}");
        return Task.FromResult(false);
    }
}

public sealed class ScreenshotEditorFakeRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<ITextRecognizer, FakeTextRecognizer>();
        services.AddSingleton<IDesktopWallpaperProvider, NoWallpapers>();
        services.AddSingleton<IKeyboardLayoutInfo, UsKeyboardLayoutInfo>();
        services.AddSingleton<IShareSheet, FakeShareSheet>();
    }
}

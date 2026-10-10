// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Capture;
using Rivet.Core.Modules;

namespace Rivet.Platform.Windows.Capture;

/// <summary>Windows implementations for the capture tools.</summary>
public sealed class CaptureRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IScreenCapturer, WindowsScreenCapturer>();
        services.AddSingleton<IWindowEnumerator, WindowsWindowEnumerator>();
        services.AddSingleton<ICaptureClipboard, WindowsCaptureClipboard>();
        services.AddSingleton<IOcrEngine, WindowsOcrEngine>();
        services.AddSingleton<ICapturePlatform, WindowsCapturePlatform>();
    }
}

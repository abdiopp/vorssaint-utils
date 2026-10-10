// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Modules;
using Rivet.Core.Recording.Engine;

namespace Rivet.Platform.Windows.Recording;

/// <summary>Screen recording: Windows Graphics Capture + Media Foundation, WASAPI, cursor shapes.</summary>
public sealed class RecordingRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IVideoCaptureBackend, WgcCaptureBackend>();
        services.AddSingleton<IAudioCaptureBackend, WasapiAudioBackend>();
        services.AddSingleton<ICursorProbe, WindowsCursorProbe>();
        services.AddSingleton<IRecorderSystem, WindowsRecorderSystem>();
        services.AddSingleton<IRawTakeExporter, MediaFoundationTakeExporter>();
        services.AddSingleton<ITakeImporter, MediaFoundationTakeImporter>();
    }
}

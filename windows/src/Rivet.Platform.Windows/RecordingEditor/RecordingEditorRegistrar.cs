// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Modules;
using Rivet.Core.RecordingEditor;

namespace Rivet.Platform.Windows.RecordingEditor;

/// <summary>Media Foundation decode/encode, WASAPI playback and the editor's shell helpers.</summary>
public sealed class RecordingEditorRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IVideoFrameSourceFactory, MfVideoFrameSourceFactory>();
        services.AddSingleton<IVideoEncoderFactory, MfMp4EncoderFactory>();
        services.AddSingleton<IAudioPlaybackFactory, WasapiPlaybackFactory>();
        services.AddSingleton<IRecordingEditorShell, WindowsRecordingEditorShell>();
    }
}

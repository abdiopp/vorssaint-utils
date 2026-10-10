// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Modules;
using Rivet.Core.Recording.Engine;

namespace Rivet.Platform.Fake.Recording;

/// <summary>Screen recording stand-ins for the development build and headless tests.</summary>
public sealed class FakeRecordingRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<FakeVideoCaptureBackend>();
        services.AddSingleton<IVideoCaptureBackend>(sp => sp.GetRequiredService<FakeVideoCaptureBackend>());
        services.AddSingleton<FakeAudioCaptureBackend>();
        services.AddSingleton<IAudioCaptureBackend>(sp => sp.GetRequiredService<FakeAudioCaptureBackend>());
        services.AddSingleton<FakeCursorProbe>();
        services.AddSingleton<ICursorProbe>(sp => sp.GetRequiredService<FakeCursorProbe>());
        services.AddSingleton<FakeRecorderSystem>();
        services.AddSingleton<IRecorderSystem>(sp => sp.GetRequiredService<FakeRecorderSystem>());
        services.AddSingleton<IRawTakeExporter, FakeRawTakeExporter>();
        services.AddSingleton<ITakeImporter, FakeTakeImporter>();
    }
}

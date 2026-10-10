// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Modules;
using Rivet.Core.Modules.MediaTools;
using Rivet.Core.Platform;

namespace Rivet.Platform.Fake.MediaTools;

/// <summary>
/// The development build has no video or OCR engine: these report "not
/// available" so the UI shows that state instead of pretending to work. The
/// image tool runs everywhere (it only needs SkiaSharp).
/// </summary>
public sealed class FakeVideoProbe : IVideoProbe
{
    public Task<VideoInfo?> ProbeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<VideoInfo?>(null);
}

public sealed class FakeVideoTranscoder : IVideoTranscoder
{
    public bool IsAvailable => false;

    public Task EncodeAsync(VideoEncodeRequest request, IProgress<double> progress, CancellationToken cancellationToken) =>
        throw new MediaJobException("Video encoding needs Windows.");
}

public sealed class FakeVideoFrameReader : IVideoFrameReader
{
    public bool IsAvailable => false;

    public Task ReadFramesAsync(string input, IReadOnlyList<double> times, MediaSize size, Func<int, PixelBuffer, Task> onFrame, CancellationToken cancellationToken) =>
        throw new MediaJobException("Video decoding needs Windows.");
}

public sealed class FakeOcrEngine : IOcrEngine
{
    public bool IsAvailable => false;

    public IReadOnlyList<string> AvailableLanguages => [];

    public Task<string> RecognizeAsync(PixelBuffer image, IReadOnlyList<string> preferredLanguages, bool accurate, CancellationToken cancellationToken) =>
        throw new MediaJobException("Text recognition needs Windows.");
}

public sealed class FakeImageCodecs : IImageCodecs
{
    public bool CanEncodeHeic => false;

    public bool CanDecodeHeic => false;

    public PixelBuffer? Decode(string path, int maxPixel) => null;

    public byte[]? EncodeHeic(PixelBuffer image, double quality) => null;
}

public sealed class MediaToolsFakeRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IVideoProbe, FakeVideoProbe>();
        services.AddSingleton<IVideoTranscoder, FakeVideoTranscoder>();
        services.AddSingleton<IVideoFrameReader, FakeVideoFrameReader>();
        services.AddSingleton<IOcrEngine, FakeOcrEngine>();
        services.AddSingleton<IImageCodecs, FakeImageCodecs>();
        services.AddSingleton<IFileIdentity, PortableFileIdentity>();
    }
}

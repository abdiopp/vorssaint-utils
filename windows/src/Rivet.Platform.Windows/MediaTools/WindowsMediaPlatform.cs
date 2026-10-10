// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;
using Rivet.Core.Modules.MediaTools;
using Rivet.Core.Platform;
using Windows.Foundation.Collections;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.MediaProperties;
using Windows.Media.Ocr;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Rivet.Platform.Windows.MediaTools;

/// <summary>Duration, size, rotation, frame rate and tracks via Windows.Storage and MediaEncodingProfile.</summary>
public sealed class WindowsVideoProbe : IVideoProbe
{
    public async Task<VideoInfo?> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
            var properties = await file.Properties.GetVideoPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);
            MediaEncodingProfile? profile = null;
            try
            {
                profile = await MediaEncodingProfile.CreateFromFileAsync(file).AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
            {
                Log.Info("media", $"No encoding profile for {path}: 0x{ex.HResult:X8}");
            }

            var rotation = properties.Orientation switch
            {
                VideoOrientation.Rotate90 => 90,
                VideoOrientation.Rotate180 => 180,
                VideoOrientation.Rotate270 => 270,
                _ => 0,
            };
            int width = (int)(profile?.Video?.Width ?? properties.Width);
            int height = (int)(profile?.Video?.Height ?? properties.Height);
            var size = rotation is 90 or 270 ? new MediaSize(height, width) : new MediaSize(width, height);
            var fps = profile?.Video?.FrameRate is { Denominator: > 0 } rate ? rate.Numerator / (double)rate.Denominator : 0;
            var hasVideo = (profile?.Video is not null || properties.Width > 0) && width > 0 && height > 0;
            return new VideoInfo
            {
                Duration = Math.Round(properties.Duration.TotalSeconds, 3),
                Size = size,
                FrameRate = fps,
                HasVideo = hasVideo,
                HasAudio = profile?.Audio is not null,
                Rotation = rotation,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException or ArgumentException or InvalidOperationException)
        {
            Log.Warn("media", $"Could not read {path}.", ex);
            return null;
        }
    }
}

/// <summary>
/// H.264/AAC MP4 through Windows.Media.Transcoding.MediaTranscoder (hardware
/// encoders when present). Trimming uses TrimStartTime and TrimStopTime (the
/// amount cut from the end). AAC uses 96 or 128 kbps, the rates the Microsoft
/// encoder accepts. MediaTranscoder does not expose the MP4 "moov before mdat"
/// option, so outputs are not guaranteed to be fast-start.
/// </summary>
public sealed class WindowsVideoTranscoder : IVideoTranscoder
{
    public bool IsAvailable => true;

    public async Task EncodeAsync(VideoEncodeRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        StorageFile source;
        StorageFile destination;
        try
        {
            source = await StorageFile.GetFileFromPathAsync(request.Input).AsTask(cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(request.Output, [], cancellationToken).ConfigureAwait(false);
            destination = await StorageFile.GetFileFromPathAsync(request.Output).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException or ArgumentException)
        {
            throw new MediaJobException(ex.Message);
        }

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        profile.Container = new ContainerEncodingProperties { Subtype = MediaEncodingSubtypes.Mpeg4 };
        profile.Video.Subtype = MediaEncodingSubtypes.H264;
        profile.Video.ProfileId = H264ProfileIds.High;
        profile.Video.Width = (uint)request.Size.Width;
        profile.Video.Height = (uint)request.Size.Height;
        profile.Video.Bitrate = (uint)Math.Max(100_000, request.VideoBitRate);
        profile.Video.FrameRate.Numerator = (uint)Math.Clamp(request.FrameRate, 1, 60);
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Audio = request.KeepAudio && request.AudioBitRate > 0
            ? AudioEncodingProperties.CreateAac(48_000, 2, (uint)(request.AudioBitRate <= 96_000 ? 96_000 : 128_000))
            : null;

        var duration = await DurationAsync(source).ConfigureAwait(false);
        var transcoder = new MediaTranscoder
        {
            HardwareAccelerationEnabled = true,
            VideoProcessingAlgorithm = MediaVideoProcessingAlgorithm.MrfCrf444,
            TrimStartTime = TimeSpan.FromSeconds(Math.Max(0, request.Start)),
        };
        if (request.End > 0 && duration > TimeSpan.Zero)
        {
            var cut = duration - TimeSpan.FromSeconds(request.End);
            transcoder.TrimStopTime = cut > TimeSpan.Zero ? cut : TimeSpan.Zero;
        }

        PrepareTranscodeResult prepared;
        try
        {
            prepared = await transcoder.PrepareFileTranscodeAsync(source, destination, profile).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            throw new MediaJobException(ex.Message);
        }

        if (!prepared.CanTranscode)
        {
            throw new MediaJobException(prepared.FailureReason switch
            {
                TranscodeFailureReason.CodecNotFound => "Encoder unavailable.",
                TranscodeFailureReason.InvalidProfile => "Video could not be encoded.",
                _ => "Video could not be read.",
            });
        }

        var reporter = new Progress<double>(percent => progress.Report(Math.Clamp(percent / 100.0, 0, 0.99)));
        try
        {
            await prepared.TranscodeAsync().AsTask(cancellationToken, reporter).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            throw new MediaJobException($"Video could not be encoded. ({ex.Message})");
        }
    }

    private static async Task<TimeSpan> DurationAsync(StorageFile file)
    {
        try
        {
            var properties = await file.Properties.GetVideoPropertiesAsync();
            return properties.Duration;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return TimeSpan.Zero;
        }
    }
}

/// <summary>
/// Text recognition with Windows.Media.Ocr: the first installed recognizer
/// among the preferred languages (exact tag, then same script/region prefix,
/// then same language), else the user's profile languages. Images larger than
/// OcrEngine.MaxImageDimension are scaled down first. Windows has no
/// Accurate/Fast distinction.
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    public bool IsAvailable => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    public IReadOnlyList<string> AvailableLanguages => OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    public async Task<string> RecognizeAsync(PixelBuffer image, IReadOnlyList<string> preferredLanguages, bool accurate, CancellationToken cancellationToken)
    {
        var engine = CreateEngine(preferredLanguages) ?? throw new MediaJobException("No text recognition language is installed. Add one in Windows Settings › Time & language › Language & region.");
        var source = Fit(image, (int)OcrEngine.MaxImageDimension);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(source.Pixels.AsBuffer(0, source.Stride * source.Height), BitmapPixelFormat.Bgra8, source.Width, source.Height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        return string.Join("\n", result.Lines.Select(l => l.Text));
    }

    private static OcrEngine? CreateEngine(IReadOnlyList<string> preferred)
    {
        var available = OcrEngine.AvailableRecognizerLanguages.ToList();
        foreach (var tag in preferred)
        {
            var match = available.FirstOrDefault(l => string.Equals(l.LanguageTag, tag, StringComparison.OrdinalIgnoreCase))
                        ?? available.FirstOrDefault(l => l.LanguageTag.StartsWith(tag + "-", StringComparison.OrdinalIgnoreCase))
                        ?? available.FirstOrDefault(l => string.Equals(PrimaryTag(l.LanguageTag), PrimaryTag(tag), StringComparison.OrdinalIgnoreCase) && !tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
            if (match is not null && OcrEngine.TryCreateFromLanguage(match) is { } engine)
            {
                return engine;
            }
        }

        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private static string PrimaryTag(string tag) => tag.Split('-')[0];

    private static PixelBuffer Fit(PixelBuffer image, int maxDimension)
    {
        var longest = Math.Max(image.Width, image.Height);
        if (maxDimension <= 0 || longest <= maxDimension)
        {
            return image;
        }

        using var skImage = Rivet.Imaging.Skia.SkiaConvert.ToImage(image);
        var factor = maxDimension / (double)longest;
        using var scaled = Rivet.Imaging.Skia.SkiaConvert.Resize(skImage, Math.Max(1, (int)(image.Width * factor)), Math.Max(1, (int)(image.Height * factor)));
        return Rivet.Imaging.Skia.SkiaConvert.ToPixelBuffer(scaled);
    }
}

/// <summary>
/// System image codecs (WIC through Windows.Graphics.Imaging): HEIC decoding
/// and encoding are offered only when the HEIF (and HEVC) extensions are
/// installed, detected from the codec lists; TIFF and other WIC formats decode
/// here when SkiaSharp cannot.
/// </summary>
public sealed class WindowsImageCodecs : IImageCodecs
{
    private readonly Lazy<bool> _canDecodeHeic = new(() => HasCodec(decoder: true));
    private readonly Lazy<bool> _canEncodeHeic = new(() => HasCodec(decoder: false));

    public bool CanEncodeHeic => _canEncodeHeic.Value;

    public bool CanDecodeHeic => _canDecodeHeic.Value;

    public PixelBuffer? Decode(string path, int maxPixel)
    {
        try
        {
            return DecodeAsync(path, maxPixel).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException or ArgumentException or InvalidOperationException)
        {
            Log.Info("media", $"Windows codecs could not decode {path}: 0x{ex.HResult:X8}");
            return null;
        }
    }

    public byte[]? EncodeHeic(PixelBuffer image, double quality)
    {
        if (!CanEncodeHeic)
        {
            return null;
        }

        try
        {
            return EncodeHeicAsync(image, quality).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            Log.Warn("media", "HEIC encoding failed.", ex);
            return null;
        }
    }

    private static bool HasCodec(bool decoder)
    {
        try
        {
            return decoder
                ? BitmapDecoder.GetDecoderInformationEnumerator().Any(i => i.CodecId == BitmapDecoder.HeifDecoderId)
                : BitmapEncoder.GetEncoderInformationEnumerator().Any(i => i.CodecId == BitmapEncoder.HeifEncoderId);
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<PixelBuffer?> DecodeAsync(string path, int maxPixel)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        var longest = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
        if (maxPixel > 0 && longest > maxPixel)
        {
            var factor = maxPixel / (double)longest;
            transform.ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * factor));
            transform.ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * factor));
        }

        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        return new PixelBuffer(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    private static async Task<byte[]> EncodeHeicAsync(PixelBuffer image, double quality)
    {
        using var stream = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(Math.Clamp(quality, 0.1, 1.0), global::Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, stream, options);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(image.Pixels.AsBuffer(0, image.Stride * image.Height), BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Premultiplied);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}

public sealed class MediaToolsWindowsRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IVideoProbe, WindowsVideoProbe>();
        services.AddSingleton<IVideoTranscoder, WindowsVideoTranscoder>();
        services.AddSingleton<IVideoFrameReader, WindowsVideoFrameReader>();
        services.AddSingleton<IOcrEngine, WindowsOcrEngine>();
        services.AddSingleton<IImageCodecs, WindowsImageCodecs>();
        services.AddSingleton<IFileIdentity, WindowsFileIdentity>();
    }
}

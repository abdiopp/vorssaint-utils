// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.Recording.Engine;
using Vortice;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Rivet.Platform.Windows.Recording;

/// <summary>
/// The master writer: H.264 High (no B-frames, 2 s key-frame interval,
/// "High" bit rate of §6.3, BT.709 tags) in an MP4 through a Media
/// Foundation sink writer. Two input paths:
/// <list type="bullet">
/// <item><b>GPU</b>: BGRA D3D11 textures as DXGI surface buffers, with the
/// app's device handed to Media Foundation through a DXGI device manager, so
/// the colour conversion (video processor) and a hardware encoder MFT work on
/// the GPU (MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS);</item>
/// <item><b>CPU</b>: BGRA bytes in memory buffers and the software encoder,
/// used when the GPU path is refused (no video support, no hardware encoder,
/// a size the encoder rejects).</item>
/// </list>
/// Frames carry their own timestamps (variable frame rate); the MP4 sink
/// derives durations from them. The container is a regular MP4 (index written
/// at <see cref="Finish"/>) for the widest compatibility with decoders and
/// editors; see the module doc for the fragmented-MP4 trade-off.
/// </summary>
internal sealed class H264Writer : IDisposable
{
    // ICodecAPI properties, passed to the encoder as sink-writer encoding parameters (codecapi.h).
    private static readonly Guid CodecApiRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");
    private static readonly Guid CodecApiMeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");
    private static readonly Guid CodecApiGopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    private static readonly Guid CodecApiBPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");
    private static readonly Guid Texture2DId = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid H264Subtype = new("34363248-0000-0010-8000-00AA00389B71"); // MFVideoFormat_H264
    // MFVideoFormat_ARGB32 = D3DFMT_A8R8G8B8, the same memory layout as the capture textures (DXGI_FORMAT_B8G8R8A8_UNORM).
    private static readonly Guid Argb32Subtype = new("00000015-0000-0010-8000-00AA00389B71");

    private const uint UnconstrainedVbr = 2; // eAVEncCommonRateControlMode_UnconstrainedVBR
    private const uint ProgressiveVideo = 2; // MFVideoInterlace_Progressive
    private const uint H264ProfileHigh = 100; // eAVEncH264VProfile_High

    private readonly IMFSinkWriter _writer;
    private readonly IMFDXGIDeviceManager? _manager;
    private readonly int _stream;
    private readonly long _frameDuration;
    private bool _finished;

    private H264Writer(IMFSinkWriter writer, IMFDXGIDeviceManager? manager, int stream, int inputWidth, int inputHeight, int fps)
    {
        _writer = writer;
        _manager = manager;
        _stream = stream;
        InputWidth = inputWidth;
        InputHeight = inputHeight;
        _frameDuration = 10_000_000L / Math.Max(1, fps);
    }

    /// <summary>Frames are handed over as GPU textures (else as CPU bytes).</summary>
    public bool UsesGpuInput => _manager is not null;

    public int InputWidth { get; }

    public int InputHeight { get; }

    /// <summary>
    /// Creates the writer. <paramref name="device"/> enables the GPU path
    /// (null forces the CPU path). Input and output sizes may differ; the sink
    /// writer's video processor scales then.
    /// </summary>
    public static H264Writer Create(string path, int inputWidth, int inputHeight, int outputWidth, int outputHeight, int fps, ID3D11Device? device)
    {
        IMFDXGIDeviceManager? manager = null;
        IMFSinkWriter? writer = null;
        try
        {
            using var attributes = MediaFactory.MFCreateAttributes(4);
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
            attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
            if (device is not null)
            {
                manager = MediaFactory.MFCreateDXGIDeviceManager();
                manager.ResetDevice(device).CheckError();
                attributes.Set(SinkWriterAttributeKeys.D3DManager, manager);
            }

            writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, attributes);

            var bitrate = EncoderBitRate.ForMaster(outputWidth, outputHeight, fps);
            var gop = EncoderBitRate.MasterGopFrames(fps);
            using var output = MediaFactory.MFCreateMediaType();
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, H264Subtype);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)Math.Min(bitrate, uint.MaxValue));
            output.Set(MediaTypeAttributeKeys.InterlaceMode, ProgressiveVideo);
            output.Set(MediaTypeAttributeKeys.FrameSize, Pack(outputWidth, outputHeight));
            output.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            output.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, H264ProfileHigh);
            output.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)gop);
            output.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u);      // MFVideoPrimaries_BT709
            output.Set(MediaTypeAttributeKeys.TransferFunction, 5u);    // MFVideoTransFunc_709
            output.Set(MediaTypeAttributeKeys.YuvMatrix, 1u);           // MFVideoTransferMatrix_BT709
            output.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u);   // MFNominalRange_16_235
            var stream = writer.AddStream(output);

            using var input = MediaFactory.MFCreateMediaType();
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, Argb32Subtype);
            input.Set(MediaTypeAttributeKeys.InterlaceMode, ProgressiveVideo);
            input.Set(MediaTypeAttributeKeys.FrameSize, Pack(inputWidth, inputHeight));
            input.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            input.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
            input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(inputWidth * 4)); // positive: top-down rows

            using var encoding = MediaFactory.MFCreateAttributes(4);
            encoding.Set(CodecApiRateControlMode, UnconstrainedVbr);
            encoding.Set(CodecApiMeanBitRate, (uint)Math.Min(bitrate, uint.MaxValue));
            encoding.Set(CodecApiGopSize, (uint)gop);
            encoding.Set(CodecApiBPictureCount, 0u);
            try
            {
                writer.SetInputMediaType(stream, input, encoding);
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                // Some drivers refuse one of the codec properties; the output type still carries the bit rate.
                Log.Info("recorder", $"The encoder refused the rate-control settings ({ex.ResultCode}); using its defaults.");
                writer.SetInputMediaType(stream, input, null);
            }

            writer.BeginWriting();
            Log.Info("recorder", $"H.264 writer: {inputWidth}×{inputHeight} → {outputWidth}×{outputHeight} @ {fps} fps, {bitrate / 1e6:0.0} Mb/s, {(device is null ? "CPU" : "GPU")} input.");
            return new H264Writer(writer, manager, stream, inputWidth, inputHeight, fps);
        }
        catch
        {
            writer?.Dispose();
            manager?.Dispose();
            throw;
        }
    }

    /// <summary>Writes a BGRA texture of the input size (GPU path). Media Foundation keeps a reference until it has encoded it.</summary>
    public void WriteTexture(ID3D11Texture2D texture, double time)
    {
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(Texture2DId, texture, 0, false);
        using (var buffer2D = buffer.QueryInterface<IMF2DBuffer>())
        {
            buffer.CurrentLength = buffer2D.ContiguousLength;
        }

        WriteSample(buffer, time);
    }

    /// <summary>Writes BGRA rows (CPU path).</summary>
    public unsafe void WriteBytes(nint pixels, int rowPitch, double time)
    {
        var rowBytes = InputWidth * 4;
        var length = rowBytes * InputHeight;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(length);
        buffer.Lock(out var destination, out _, out _);
        try
        {
            for (var y = 0; y < InputHeight; y++)
            {
                Buffer.MemoryCopy((void*)(pixels + (y * rowPitch)), (void*)(destination + (y * rowBytes)), rowBytes, rowBytes);
            }
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = length;
        WriteSample(buffer, time);
    }

    /// <summary>Finalizes the file (writes the MP4 index). Blocks until the encoder is drained.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _writer.Finalize();
    }

    public void Dispose()
    {
        _writer.Dispose();
        _manager?.Dispose();
    }

    private void WriteSample(IMFMediaBuffer buffer, double time)
    {
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = (long)Math.Round(Math.Max(0, time) * 10_000_000);
        sample.SampleDuration = _frameDuration;
        _writer.WriteSample(_stream, sample);
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
}

/// <summary>Media Foundation start-up, matched with a shutdown per user.</summary>
internal static class MediaFoundationRuntime
{
    private static RecorderAvailability? _availability;

    public static void Startup() => MediaFactory.MFStartup(false).CheckError();

    public static void Shutdown()
    {
        try
        {
            MediaFactory.MFShutdown();
        }
        catch (Exception ex)
        {
            Log.Warn("recorder", "MFShutdown failed.", ex);
        }
    }

    /// <summary>Media Foundation is missing on Windows "N" editions without the Media Feature Pack.</summary>
    public static RecorderAvailability Probe()
    {
        if (_availability is { } known)
        {
            return known;
        }

        try
        {
            Startup();
            Shutdown();
            _availability = RecorderAvailability.Available;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or SharpGen.Runtime.SharpGenException)
        {
            Log.Warn("recorder", "Media Foundation is unavailable.", ex);
            _availability = RecorderAvailability.EncoderUnavailable;
        }

        return _availability.Value;
    }
}

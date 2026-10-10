// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Rivet.Core.Diagnostics;
using Rivet.Core.Recording.Engine;

namespace Rivet.Platform.Windows.Recording;

/// <summary>
/// WASAPI capture through NAudio's recorder (event-driven shared mode; each
/// packet carries the capture client's QPC position, the shared clock).
/// <list type="bullet">
/// <item><b>System audio</b>: process loopback with "exclude target process
/// tree" = this app, so the recording does not hear the app's own sounds
/// (Windows 10 2004+ per NAudio; Microsoft documents build 20348). When that
/// is refused, endpoint loopback of the default output, re-opened when the
/// default output changes or disappears (this app's own sounds are included
/// then).</item>
/// <item><b>Microphone</b>: the default input with automatic stream routing
/// (follows default-device changes), or a chosen endpoint.</item>
/// </list>
/// The engine is asked for 48 kHz stereo 16-bit (AUTOCONVERTPCM); if an
/// endpoint refuses, its mix format is captured and the Core converter folds
/// and resamples it. Loopback delivers nothing while the PC is silent; the
/// track writer fills those gaps.
/// </summary>
public sealed class WasapiAudioBackend : IAudioCaptureBackend
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);

    public async Task<IAudioCaptureSession> OpenSystemAudioAsync(CancellationToken cancellationToken)
    {
        WasapiRecorder? processLoopback = null;
        try
        {
            processLoopback = await new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree)
                .WithFormat(new WaveFormat(PcmFormat.Take.SampleRate, PcmFormat.Take.BitsPerSample, PcmFormat.Take.Channels))
                .BuildAsync()
                .WaitAsync(ActivationTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info("recorder", $"Process loopback unavailable ({ex.GetType().Name}: {ex.Message}); recording the default output instead.");
        }

        return new SystemAudioSession(processLoopback);
    }

    public async Task<IAudioCaptureSession> OpenMicrophoneAsync(string? deviceId, CancellationToken cancellationToken)
    {
        switch (ProbeMicrophone(deviceId))
        {
            case MicrophoneStatus.Denied:
                throw new UnauthorizedAccessException("Windows privacy settings block desktop apps from the microphone.");
            case MicrophoneStatus.NoDevice:
                throw new InvalidOperationException("No microphone is connected.");
        }

        var format = new WaveFormat(PcmFormat.Take.SampleRate, PcmFormat.Take.BitsPerSample, PcmFormat.Take.Channels);
        WasapiRecorder recorder;
        string description;
        var device = string.IsNullOrEmpty(deviceId) ? null : FindActiveDevice(deviceId, DataFlow.Capture);
        if (device is not null)
        {
            description = device.FriendlyName;
            recorder = new WasapiRecorderBuilder().WithDevice(device).WithFormat(format).Build();
        }
        else
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                Log.Info("recorder", "The chosen microphone is not connected; using the default input.");
            }

            description = "default input";
            recorder = await new WasapiRecorderBuilder()
                .WithDefaultDeviceStreamRouting()
                .WithFormat(format)
                .BuildAsync()
                .WaitAsync(ActivationTimeout, cancellationToken)
                .ConfigureAwait(false);
        }

        return new RecorderSession(description, recorder);
    }

    public MicrophoneStatus ProbeMicrophone(string? deviceId)
    {
        if (IsMicrophoneBlocked())
        {
            return MicrophoneStatus.Denied;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrEmpty(deviceId) && FindActiveDevice(deviceId, DataFlow.Capture) is { } device)
            {
                device.Dispose();
                return MicrophoneStatus.Available;
            }

            return enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console) ? MicrophoneStatus.Available : MicrophoneStatus.NoDevice;
        }
        catch (Exception ex)
        {
            Log.Warn("recorder", "Checking the microphone failed.", ex);
            return MicrophoneStatus.NoDevice;
        }
    }

    public IReadOnlyList<AudioDeviceInfo> ListMicrophones()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            if (enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out var defaultDevice) && defaultDevice is not null)
            {
                defaultId = defaultDevice.ID;
                defaultDevice.Dispose();
            }

            var result = new List<AudioDeviceInfo>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
                device.Dispose();
            }

            return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("recorder", "Listing microphones failed.", ex);
            return [];
        }
    }

    /// <summary>
    /// "Let desktop apps access your microphone" (and the global switch) live in
    /// the capability consent store; a Deny there makes capture fail with
    /// E_ACCESSDENIED, so it is checked up front to show the right message.
    /// </summary>
    internal static bool IsMicrophoneBlocked()
    {
        const string store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        try
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var path in new[] { store, store + @"\NonPackaged" })
                {
                    using var key = root.OpenSubKey(path);
                    if (key?.GetValue("Value") is string value && value.Equals("Deny", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return false;
    }

    internal static MMDevice? FindActiveDevice(string id, DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(id);
            if (device.DataFlow == flow && device.State == DeviceState.Active)
            {
                return device;
            }

            device.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        return null;
    }

    internal static PcmFormat ToPcmFormat(WaveFormat format)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                      || (format is WaveFormatExtensible extensible && extensible.SubFormat == FloatSubFormat);
        return new PcmFormat(format.SampleRate, format.Channels, format.BitsPerSample, isFloat);
    }

    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
}

/// <summary>One NAudio recorder exposed as an <see cref="IAudioCaptureSession"/>.</summary>
internal sealed class RecorderSession : IAudioCaptureSession
{
    private readonly object _gate = new();
    private WasapiRecorder? _recorder;
    private PcmFormat _format;
    private volatile bool _stopped;
    private int _failed;
    private int _loggedPacketError;

    public RecorderSession(string description, WasapiRecorder recorder)
    {
        Description = description;
        _recorder = recorder;
        recorder.DataAvailable += OnData;
        recorder.RecordingStopped += OnStopped;
    }

    public string Description { get; }

    public event AudioPacketHandler? PacketAvailable;

    public event EventHandler<string>? Failed;

    public void Start()
    {
        var recorder = _recorder ?? throw new ObjectDisposedException(nameof(RecorderSession));
        _format = WasapiAudioBackend.ToPcmFormat(recorder.WaveFormat);
        recorder.StartRecording();
        _format = WasapiAudioBackend.ToPcmFormat(recorder.WaveFormat);
        if (!_format.IsValid)
        {
            throw new NotSupportedException($"Unsupported capture format {recorder.WaveFormat}.");
        }
    }

    public void Stop()
    {
        _stopped = true;
        var recorder = _recorder;
        if (recorder is null)
        {
            return;
        }

        try
        {
            recorder.StopRecording();
        }
        catch (Exception ex)
        {
            Log.Warn("recorder", $"Stopping {Description} failed.", ex);
        }

        lock (_gate)
        {
            PacketAvailable = null;
        }
    }

    public void Dispose()
    {
        Stop();
        var recorder = Interlocked.Exchange(ref _recorder, null);
        if (recorder is not null)
        {
            recorder.DataAvailable -= OnData;
            recorder.RecordingStopped -= OnStopped;
            try
            {
                recorder.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("recorder", $"Releasing {Description} failed.", ex);
            }
        }
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (_stopped || buffer.IsEmpty)
        {
            return;
        }

        var format = _format;
        if (!format.IsValid)
        {
            return;
        }

        var hostTime = qpcPosition > 0 && (flags & AudioClientBufferFlags.TimestampError) == 0
            ? Core.Recording.Engine.QpcClock.FromHundredNanoseconds(qpcPosition)
            : Core.Recording.Engine.QpcClock.Instance.Now - (buffer.Length / format.BlockAlign / (double)format.SampleRate);
        try
        {
            lock (_gate)
            {
                PacketAvailable?.Invoke(buffer, format, hostTime, (flags & AudioClientBufferFlags.Silent) != 0);
            }
        }
        catch (Exception ex)
        {
            // One bad packet must not end the capture thread.
            if (Interlocked.Exchange(ref _loggedPacketError, 1) == 0)
            {
                Log.Warn("recorder", $"Handling audio from {Description} failed.", ex);
            }
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (!_stopped && e.Exception is { } error && Interlocked.Exchange(ref _failed, 1) == 0)
        {
            Failed?.Invoke(this, $"{error.GetType().Name}: {error.Message}");
        }
    }
}

/// <summary>
/// System audio with its fallbacks: process loopback while it works, else
/// endpoint loopback of the default output, re-opened on default-device
/// changes. Packets of every underlying recorder go to the same handler; the
/// track writer keeps them on the timeline.
/// </summary>
internal sealed class SystemAudioSession : IAudioCaptureSession
{
    private readonly object _gate = new();
    private RecorderSession? _current;
    private WasapiRecorder? _pendingProcessLoopback;
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private volatile bool _stopped;
    private int _reopenScheduled;
    private int _failedRaised;

    public SystemAudioSession(WasapiRecorder? processLoopback)
    {
        _pendingProcessLoopback = processLoopback;
        Description = processLoopback is null ? "default output (loopback)" : "process loopback, this app excluded";
    }

    public string Description { get; private set; }

    public event AudioPacketHandler? PacketAvailable;

    public event EventHandler<string>? Failed;

    public void Start()
    {
        if (_pendingProcessLoopback is { } processLoopback)
        {
            _pendingProcessLoopback = null;
            var session = new RecorderSession("process loopback", processLoopback);
            try
            {
                Attach(session);
                session.Start();
                return;
            }
            catch (Exception ex)
            {
                Log.Info("recorder", $"Process loopback did not start ({ex.Message}); recording the default output instead.");
                Detach(session);
                session.Dispose();
            }
        }

        StartEndpointLoopback(throwOnFailure: true);
    }

    public void Stop()
    {
        _stopped = true;
        _notifications?.Dispose();
        _notifications = null;
        RecorderSession? current;
        lock (_gate)
        {
            current = _current;
        }

        current?.Stop();
    }

    public void Dispose()
    {
        Stop();
        RecorderSession? current;
        lock (_gate)
        {
            current = _current;
            _current = null;
        }

        current?.Dispose();
        _pendingProcessLoopback?.Dispose();
        _pendingProcessLoopback = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    private void StartEndpointLoopback(bool throwOnFailure)
    {
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            if (_notifications is null)
            {
                _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
                _notifications.DefaultDeviceChanged += (_, e) =>
                {
                    if (e.Flow == DataFlow.Render && e.Role == Role.Console)
                    {
                        ScheduleReopen("default output changed");
                    }
                };
            }

            var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            var name = device.FriendlyName;
            var session = TryCreateLoopback(device, withFormat: true) ?? TryCreateLoopback(device, withFormat: false)
                ?? throw new InvalidOperationException($"Loopback capture of {name} failed.");
            Description = $"loopback of {name}";
            Log.Info("recorder", $"System audio: {Description}.");
        }
        catch (Exception ex)
        {
            if (throwOnFailure)
            {
                throw;
            }

            RaiseFailed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private RecorderSession? TryCreateLoopback(MMDevice device, bool withFormat)
    {
        RecorderSession? session = null;
        try
        {
            var builder = new WasapiRecorderBuilder().WithLoopbackCapture().WithDevice(device);
            if (withFormat)
            {
                builder = builder.WithFormat(new WaveFormat(PcmFormat.Take.SampleRate, PcmFormat.Take.BitsPerSample, PcmFormat.Take.Channels));
            }

            session = new RecorderSession($"loopback of {device.FriendlyName}", builder.Build());
            Attach(session);
            session.Start();
            return session;
        }
        catch (Exception ex)
        {
            Log.Info("recorder", $"Loopback {(withFormat ? "at 48 kHz" : "in the mix format")} failed: {ex.Message}");
            if (session is not null)
            {
                Detach(session);
                session.Dispose();
            }

            return null;
        }
    }

    private void Attach(RecorderSession session)
    {
        session.PacketAvailable += Forward;
        session.Failed += (_, reason) => ScheduleReopen(reason);
        RecorderSession? previous;
        lock (_gate)
        {
            previous = _current;
            _current = session;
        }

        if (previous is not null && previous != session)
        {
            previous.Dispose();
        }
    }

    private void Detach(RecorderSession session)
    {
        session.PacketAvailable -= Forward;
        lock (_gate)
        {
            if (_current == session)
            {
                _current = null;
            }
        }
    }

    private void Forward(ReadOnlySpan<byte> data, PcmFormat format, double hostTime, bool silent) =>
        PacketAvailable?.Invoke(data, format, hostTime, silent);

    private void ScheduleReopen(string reason)
    {
        if (_stopped || Interlocked.Exchange(ref _reopenScheduled, 1) == 1)
        {
            return;
        }

        Log.Info("recorder", $"Re-opening system audio: {reason}.");
        _ = Task.Run(async () =>
        {
            try
            {
                // Let Windows finish switching devices.
                await Task.Delay(300).ConfigureAwait(false);
                if (!_stopped)
                {
                    StartEndpointLoopback(throwOnFailure: false);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reopenScheduled, 0);
            }
        });
    }

    private void RaiseFailed(string reason)
    {
        if (!_stopped && Interlocked.Exchange(ref _failedRaised, 1) == 0)
        {
            Failed?.Invoke(this, reason);
        }
    }
}

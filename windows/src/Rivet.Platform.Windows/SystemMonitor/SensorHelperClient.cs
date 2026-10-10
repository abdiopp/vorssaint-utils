// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.SystemMonitor;

namespace Rivet.Platform.Windows.SystemMonitor;

/// <summary>
/// The app's side of the elevated sensor helper (src/Rivet.SensorHelper).
/// <para>
/// The helper is a separate process that Task Scheduler runs as SYSTEM from
/// Program Files. It reads the processor's own temperature sensors, which
/// need administrator rights, so this app does not have to run elevated.
/// This class starts the task when a reading is wanted, asks over a named
/// pipe, and installs or removes the helper (one administrator prompt each).
/// </para>
/// Reads are called from the sampler's thread and may block for a few
/// seconds while the helper starts; a helper that does not answer is left
/// alone for a growing while.
/// </summary>
public sealed class SensorHelperClient : ISensorHelper, IDisposable
{
    private static readonly TimeSpan ConnectRunning = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ConnectStarting = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstAnswer = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LongestBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(60);
    private static readonly byte[] CpuRequest = Encoding.ASCII.GetBytes(SensorHelperProtocol.CpuRequest + "\n");

    private readonly object _gate = new();
    private readonly HardwareSensors _hardware;
    private readonly string _installedExe;
    private readonly string _bundledExe;
    private NamedPipeClientStream? _pipe;
    private BoundedLineReader? _reader;
    private bool _answered;
    private bool _incompatible;
    private bool _settingUp;
    private bool _disposed;
    private DateTime _retryAfter = DateTime.MinValue;
    private TimeSpan _backoff = FirstBackoff;
    private DateTime _lastValue = DateTime.MinValue;

    public SensorHelperClient(HardwareSensors hardware)
    {
        _hardware = hardware;
        var name = SensorHelperProtocol.ExecutableName(AppIdentity.Id);
        _bundledExe = Path.Combine(AppContext.BaseDirectory, name);
        _installedExe = Path.Combine(
            SensorHelperProtocol.InstallFolder(AppIdentity.Id, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            name);
    }

    public event EventHandler? StateChanged;

    public SensorHelperState State
    {
        get
        {
            if (!HardwareSensors.IsSupported)
            {
                return SensorHelperState.Unsupported;
            }

            lock (_gate)
            {
                if (File.Exists(_installedExe) && !_incompatible)
                {
                    return SensorHelperState.Installed;
                }
            }

            // Without the bundled copy there is nothing to install from (a development build).
            return File.Exists(_bundledExe) ? SensorHelperState.NotInstalled : SensorHelperState.Unsupported;
        }
    }

    public bool DriverInstalled => _hardware.SensorDriverInstalled;

    /// <summary>The helper gave a temperature a moment ago, so a miss now is a hiccup, not an absence.</summary>
    public bool WorkedRecently
    {
        get
        {
            lock (_gate)
            {
                return DateTime.UtcNow - _lastValue < RecentWindow;
            }
        }
    }

    /// <summary>The hottest processor's temperature in °C from the helper, or null when it is not set up or does not answer.</summary>
    public double? ReadCpuTemperature()
    {
        if (!HardwareSensors.IsSupported || !_hardware.SensorDriverInstalled)
        {
            return null;
        }

        lock (_gate)
        {
            if (_disposed || _settingUp || _incompatible || DateTime.UtcNow < _retryAfter || !File.Exists(_installedExe))
            {
                return null;
            }

            // A connection kept from an earlier read may be stale (the helper exits when idle): try once more on a new one.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var reused = _pipe is not null;
                if (!reused && !Connect())
                {
                    break;
                }

                try
                {
                    var value = Request();
                    _backoff = FirstBackoff;
                    if (value is not null)
                    {
                        _lastValue = DateTime.UtcNow;
                    }

                    return value;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException)
                {
                    Drop();
                    if (!reused || _incompatible)
                    {
                        Log.Warn("monitor", "The sensor helper did not answer.", ex);
                        break;
                    }
                }
            }

            _retryAfter = DateTime.UtcNow + _backoff;
            _backoff = TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, LongestBackoff.Ticks));
            return null;
        }
    }

    public Task<SensorHelperResult> InstallAsync() => Task.Run(() =>
    {
        var user = WindowsIdentity.GetCurrent().User?.Value;
        return user is null ? SensorHelperResult.Failed : RunSetup("--install", "--user", user);
    });

    public Task<SensorHelperResult> RemoveAsync() => Task.Run(() => RunSetup("--uninstall"));

    /// <summary>Runs the bundled helper elevated (one UAC prompt) and waits for it.</summary>
    private SensorHelperResult RunSetup(params string[] arguments)
    {
        var result = SensorHelperResult.Failed;
        try
        {
            lock (_gate)
            {
                // Let go of the pipe and stay away, so the running helper can be stopped and replaced.
                _settingUp = true;
                Drop();
            }

            using var process = Process.Start(new ProcessStartInfo(_bundledExe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', arguments),
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null || !process.WaitForExit(SetupTimeout))
            {
                Log.Warn("monitor", "The sensor helper's setup did not finish.");
            }
            else if (process.ExitCode != 0)
            {
                Log.Warn("monitor", $"The sensor helper's setup failed with code {process.ExitCode}.");
            }
            else
            {
                result = SensorHelperResult.Done;
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The person declined the UAC prompt.
            result = SensorHelperResult.Cancelled;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn("monitor", "Could not start the sensor helper's setup.", ex);
        }

        lock (_gate)
        {
            _settingUp = false;
            _incompatible = false;
            _answered = false;
            _retryAfter = DateTime.MinValue;
            _backoff = FirstBackoff;
            _lastValue = DateTime.MinValue;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private bool Connect()
    {
        if (TryConnect(ConnectRunning))
        {
            return true;
        }

        return StartTask() && TryConnect(ConnectStarting);
    }

    private bool TryConnect(TimeSpan timeout)
    {
        // Identification: the helper may see who we are, never act as us.
        var pipe = new NamedPipeClientStream(".", SensorHelperProtocol.PipeName(AppIdentity.Id), PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(timeout);
            if (!IsHelper(pipe))
            {
                pipe.Dispose();
                return false;
            }

            _pipe = pipe;
            _reader = new BoundedLineReader(pipe, 256);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            pipe.Dispose();
            return false;
        }
    }

    /// <summary>
    /// The pipe was created by SYSTEM or an administrator, not by some other
    /// program of this user that took the name to feed us numbers.
    /// </summary>
    private static bool IsHelper(NamedPipeClientStream pipe)
    {
        try
        {
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is not null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
            {
                return true;
            }

            Log.Warn("monitor", $"The sensor helper's pipe belongs to {owner?.Value ?? "nobody"}; ignoring it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("monitor", "Could not check who owns the sensor helper's pipe.", ex);
        }

        return false;
    }

    /// <summary>Asks Task Scheduler to start the helper; the task's access list allows this user to.</summary>
    private static bool StartTask()
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("/Run");
            start.ArgumentList.Add("/TN");
            start.ArgumentList.Add(SensorHelperProtocol.TaskName(AppIdentity.Id));
            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                Log.Warn("monitor", "Starting the sensor helper's task timed out.");
                return false;
            }

            if (process.ExitCode != 0)
            {
                Log.Warn("monitor", $"Starting the sensor helper's task failed with code {process.ExitCode}.");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn("monitor", "Could not start the sensor helper's task.", ex);
            return false;
        }
    }

    private double? Request()
    {
        var pipe = _pipe!;
        var reader = _reader!;

        // The first answer waits for the helper to open its sensors.
        using var timeout = new CancellationTokenSource(_answered ? Answer : FirstAnswer);
        pipe.WriteAsync(CpuRequest, timeout.Token).AsTask().GetAwaiter().GetResult();
        pipe.FlushAsync(timeout.Token).GetAwaiter().GetResult();

        var header = reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
        if (!SensorHelperProtocol.TryParseHeader(header, out var version))
        {
            throw new InvalidDataException("Unexpected answer from the sensor helper.");
        }

        if (version != SensorHelperProtocol.Version)
        {
            // An older or newer helper: offer to install the bundled one again.
            _incompatible = true;
            ThreadPool.QueueUserWorkItem(_ => StateChanged?.Invoke(this, EventArgs.Empty));
            throw new InvalidDataException($"The installed sensor helper speaks version {version}.");
        }

        var processors = new Dictionary<int, List<NamedTemperature>>();
        for (var lines = 0; ; lines++)
        {
            var line = reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            if (line == SensorHelperProtocol.End)
            {
                break;
            }

            if (line is null || lines >= SensorHelperProtocol.MaxResponseLines)
            {
                throw new InvalidDataException("The sensor helper's answer was cut short.");
            }

            if (SensorHelperProtocol.TryParseSensor(line, out var hardware, out var name, out var celsius))
            {
                if (!processors.TryGetValue(hardware, out var sensors))
                {
                    processors[hardware] = sensors = [];
                }

                sensors.Add(new NamedTemperature(name, celsius));
            }
        }

        _answered = true;
        double? hottest = null;
        foreach (var sensors in processors.Values)
        {
            if (TemperatureSelection.Cpu(sensors) is { } value && (hottest is null || value > hottest))
            {
                hottest = value;
            }
        }

        return hottest;
    }

    private void Drop()
    {
        _pipe?.Dispose();
        _pipe = null;
        _reader = null;
        _answered = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Drop();
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using LibreHardwareMonitor.Hardware;
using Rivet.Core.SystemMonitor;

namespace Rivet.SensorHelper;

/// <summary>
/// Serves the processor's temperature sensors on a named pipe that only the
/// given user may open, one client at a time, and exits when nobody has asked
/// for a while. The only request is the word "cpu"; anything else drops the
/// client.
/// </summary>
internal static class SensorServer
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(SecurityIdentifier user)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        NamedPipeServerStream pipe;
        try
        {
            // FirstPipeInstance: fail instead of joining a pipe somebody else created under this name.
            pipe = NamedPipeServerStreamAcl.Create(
                SensorHelperProtocol.PipeName(HelperIdentity.ProductId),
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
                1024,
                16 * 1024,
                security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HelperLog.Write($"The pipe is taken (another helper is running?): {ex.Message}");
            return ExitCodes.Failed;
        }

        HelperLog.Write("Serving.");
        using (pipe)
        using (var sensors = new CpuSensors())
        {
            while (true)
            {
                using (var idle = new CancellationTokenSource(SensorHelperProtocol.IdleExit))
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(idle.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        HelperLog.Write("Idle, exiting.");
                        return ExitCodes.Done;
                    }
                    catch (IOException)
                    {
                        // The client connected and left before we looked.
                        Disconnect(pipe);
                        continue;
                    }
                }

                try
                {
                    await ServeAsync(pipe, sensors);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // The client went away or stalled.
                }

                Disconnect(pipe);
            }
        }
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, CpuSensors sensors)
    {
        var reader = new BoundedLineReader(pipe, SensorHelperProtocol.MaxRequestLength);
        while (true)
        {
            string? request;
            using (var idle = new CancellationTokenSource(SensorHelperProtocol.IdleExit))
            {
                request = await reader.ReadLineAsync(idle.Token);
            }

            if (request != SensorHelperProtocol.CpuRequest)
            {
                return;
            }

            var answer = new StringBuilder();
            answer.Append(SensorHelperProtocol.Header).Append('\n');
            foreach (var line in sensors.Read())
            {
                answer.Append(line).Append('\n');
            }

            answer.Append(SensorHelperProtocol.End).Append('\n');
            using var writing = new CancellationTokenSource(WriteTimeout);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(answer.ToString()), writing.Token);
            await pipe.FlushAsync(writing.Token);
        }
    }

    private static void Disconnect(NamedPipeServerStream pipe)
    {
        try
        {
            pipe.Disconnect();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Not connected any more.
        }
    }
}

/// <summary>The processors' temperature sensors, opened on the first request.</summary>
internal sealed class CpuSensors : IDisposable
{
    private Computer? _computer;
    private bool _failed;

    /// <summary>One protocol line per temperature sensor that has a value.</summary>
    public List<string> Read()
    {
        var lines = new List<string>();
        if (Open() is not { } computer)
        {
            return lines;
        }

        var index = 0;
        foreach (var hardware in computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Cpu)
            {
                continue;
            }

            try
            {
                hardware.Update();
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType == SensorType.Temperature && sensor.Value is { } value && float.IsFinite(value))
                    {
                        lines.Add(SensorHelperProtocol.FormatSensor(index, sensor.Name, value));
                    }
                }
            }
            catch (Exception ex)
            {
                HelperLog.Write($"Reading {hardware.Name} failed: {ex.Message}");
            }

            index++;
        }

        return lines;
    }

    private Computer? Open()
    {
        if (_computer is not null || _failed)
        {
            return _computer;
        }

        try
        {
            var computer = new Computer { IsCpuEnabled = true };
            computer.Open();
            _computer = computer;
            HelperLog.Write($"Sensors opened: {string.Join(", ", computer.Hardware.Select(h => h.Name))}.");
        }
        catch (Exception ex)
        {
            _failed = true;
            HelperLog.Write($"The sensor library could not start: {ex}");
        }

        return _computer;
    }

    public void Dispose()
    {
        try
        {
            _computer?.Close();
        }
        catch (Exception ex)
        {
            HelperLog.Write($"Closing the sensor library failed: {ex.Message}");
        }

        _computer = null;
    }
}

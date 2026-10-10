// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Rivet.Core.SystemMonitor;

/// <summary>
/// Names and wire format shared by the app and the elevated sensor helper.
/// This file is also compiled into the helper, so it depends on nothing else.
/// <para>
/// The helper serves one client at a time on a named pipe. The client sends
/// <c>cpu</c> and a line feed; the helper answers <c>ok &lt;version&gt;</c>, one
/// line per temperature sensor and <c>end</c>. There are no other requests:
/// the helper takes no input beyond that word.
/// </para>
/// </summary>
public static class SensorHelperProtocol
{
    /// <summary>Raised when the wire format changes; a helper of another version is reinstalled.</summary>
    public const int Version = 1;

    public const string CpuRequest = "cpu";
    public const string End = "end";

    /// <summary>Longest request line the helper reads before it drops the client.</summary>
    public const int MaxRequestLength = 32;

    /// <summary>Longest answer (lines) a client reads before it drops the helper.</summary>
    public const int MaxResponseLines = 1024;

    /// <summary>The helper exits after this long without a request.</summary>
    public static readonly TimeSpan IdleExit = TimeSpan.FromSeconds(90);

    public static string PipeName(string productId) => $"{productId}.SensorHelper";

    /// <summary>Task Scheduler path of the task that starts the helper.</summary>
    public static string TaskName(string productId) => $@"\{productId}\SensorHelper";

    public static string ExecutableName(string productId) => $"{productId}.SensorHelper.exe";

    /// <summary>Where "install" puts the helper: under Program Files, writable by administrators only.</summary>
    public static string InstallFolder(string productId, string programFiles) => Path.Combine(programFiles, productId, "SensorHelper");

    public static string Header => string.Create(CultureInfo.InvariantCulture, $"ok {Version}");

    public static bool TryParseHeader(string? line, out int version)
    {
        version = 0;
        return line is not null
            && line.StartsWith("ok ", StringComparison.Ordinal)
            && int.TryParse(line.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    /// <summary>One sensor: <c>t</c>, the processor index, °C and the sensor's name, tab separated.</summary>
    public static string FormatSensor(int hardware, string name, double celsius)
    {
        var clean = name.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return string.Create(CultureInfo.InvariantCulture, $"t\t{hardware}\t{celsius:R}\t{clean}");
    }

    public static bool TryParseSensor(string? line, out int hardware, out string name, out double celsius)
    {
        hardware = 0;
        name = string.Empty;
        celsius = 0;
        if (line is null)
        {
            return false;
        }

        var parts = line.Split('\t', 4);
        if (parts.Length != 4 || parts[0] != "t"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out hardware)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out celsius)
            || !double.IsFinite(celsius))
        {
            return false;
        }

        name = parts[3];
        return true;
    }
}

/// <summary>
/// Reads line-feed terminated UTF-8 lines from a stream with a hard length
/// limit, so neither side can make the other buffer without bound.
/// </summary>
public sealed class BoundedLineReader
{
    private readonly Stream _stream;
    private readonly byte[] _line;
    private readonly byte[] _buffer = new byte[256];
    private int _start;
    private int _end;

    public BoundedLineReader(Stream stream, int maxLength)
    {
        _stream = stream;
        _line = new byte[maxLength];
    }

    /// <summary>The next line without its terminator; null at the end of the stream or when a line is too long.</summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellation)
    {
        var length = 0;
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await _stream.ReadAsync(_buffer, cancellation).ConfigureAwait(false);
                if (_end == 0)
                {
                    return null;
                }
            }

            var value = _buffer[_start++];
            if (value == (byte)'\n')
            {
                return System.Text.Encoding.UTF8.GetString(_line, 0, length).TrimEnd('\r');
            }

            if (length == _line.Length)
            {
                return null;
            }

            _line[length++] = value;
        }
    }
}

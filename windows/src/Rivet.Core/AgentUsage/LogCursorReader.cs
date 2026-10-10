// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Serialization;

namespace Rivet.Core.Agents;

/// <summary>Where reading of one log stopped (spec 07 §3.8.4 "Per-file cursor").</summary>
public sealed class LogCursor
{
    public required string Path { get; init; }

    public required AgentProvider Provider { get; init; }

    /// <summary>Byte offset just after the last complete line read.</summary>
    public long Offset { get; set; }

    /// <summary>File identity (NTFS file id / inode); 0 = unknown.</summary>
    public ulong Identity { get; set; }

    /// <summary>Inside a line longer than the maximum: skip until the next newline.</summary>
    public bool Discarding { get; set; }

    public DateTimeOffset Modified { get; set; }

    /// <summary>FNV-1a-64 over the offset and the first and last 4 KiB before it.</summary>
    public ulong Fingerprint { get; set; }

    public string ParserState { get; set; } = string.Empty;

    /// <summary>Size seen at the last read (polling compares it).</summary>
    public long Size { get; set; }

    [JsonIgnore]
    public IAgentLogParser? Parser { get; set; }

    /// <summary>The parser, created (and restored from <see cref="ParserState"/>) on first use.</summary>
    public IAgentLogParser EnsureParser()
    {
        if (Parser is null)
        {
            Parser = AgentLogParsers.Create(Provider, Path);
            if (ParserState.Length > 0)
            {
                try
                {
                    Parser.LoadState(ParserState);
                }
                catch (System.Text.Json.JsonException)
                {
                    Parser = AgentLogParsers.Create(Provider, Path);
                }
            }
        }

        return Parser;
    }
}

public enum LogReadOutcome
{
    Unchanged,
    Appended,

    /// <summary>The file was replaced, truncated or rewritten in place: read again from the start.</summary>
    Restarted,
    Missing,
}

/// <summary>
/// Reads what was appended to a JSONL log since its cursor, in 4 MiB chunks,
/// split on newlines. A trailing partial line waits for its newline; a line
/// over 32 MiB is skipped. Files are opened with full sharing because agents
/// keep their logs open (and may replace them by delete + rename).
/// </summary>
public static class LogCursorReader
{
    public const int ChunkSize = 4 * 1024 * 1024;
    public const int MaxLineBytes = 32 * 1024 * 1024;
    public const int FingerprintSpan = 4096;
    private const ulong FnvOffset = 0xCBF29CE484222325;
    private const ulong FnvPrime = 0x100000001B3;

    /// <summary>Reads appended lines into <paramref name="output"/> through the cursor's parser.</summary>
    public static LogReadOutcome Read(LogCursor cursor, Func<string, ulong> identityOf, List<AgentEntry> output, CancellationToken cancel = default)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(cursor.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LogReadOutcome.Missing;
        }

        using (stream)
        {
            var size = stream.Length;
            var identity = identityOf(cursor.Path);
            var restart = (cursor.Identity != 0 && identity != 0 && identity != cursor.Identity)
                          || size < cursor.Offset
                          || (size > cursor.Offset && cursor.Offset > 0 && Fingerprint(stream, cursor.Offset) != cursor.Fingerprint);
            if (restart)
            {
                cursor.Offset = 0;
                cursor.Discarding = false;
                cursor.ParserState = string.Empty;
                cursor.Parser = AgentLogParsers.Create(cursor.Provider, cursor.Path);
            }

            cursor.Identity = identity;
            cursor.Size = size;
            try
            {
                cursor.Modified = File.GetLastWriteTimeUtc(cursor.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            if (size == cursor.Offset)
            {
                return restart ? LogReadOutcome.Restarted : LogReadOutcome.Unchanged;
            }

            ReadLines(stream, cursor, size, output, cancel);
            cursor.Fingerprint = Fingerprint(stream, cursor.Offset);
            cursor.ParserState = cursor.EnsureParser().SaveState();
            return restart ? LogReadOutcome.Restarted : LogReadOutcome.Appended;
        }
    }

    /// <summary>FNV-1a-64 over the offset (8 bytes, little-endian) and the first and last 4 KiB before it.</summary>
    public static ulong Fingerprint(Stream stream, long offset)
    {
        var hash = FnvOffset;
        Span<byte> number = stackalloc byte[8];
        BitConverter.TryWriteBytes(number, offset);
        if (!BitConverter.IsLittleEndian)
        {
            number.Reverse();
        }

        hash = Fnv(hash, number);
        var head = (int)Math.Min(FingerprintSpan, offset);
        var buffer = new byte[FingerprintSpan];
        if (head > 0)
        {
            stream.Seek(0, SeekOrigin.Begin);
            stream.ReadExactly(buffer, 0, head);
            hash = Fnv(hash, buffer.AsSpan(0, head));
            var tailStart = Math.Max(0, offset - FingerprintSpan);
            var tail = (int)(offset - tailStart);
            stream.Seek(tailStart, SeekOrigin.Begin);
            stream.ReadExactly(buffer, 0, tail);
            hash = Fnv(hash, buffer.AsSpan(0, tail));
        }

        return hash;
    }

    public static ulong Fnv(ulong hash, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        return hash;
    }

    private static void ReadLines(FileStream stream, LogCursor cursor, long size, List<AgentEntry> output, CancellationToken cancel)
    {
        var parser = cursor.EnsureParser();
        var fileTime = new DateTimeOffset(cursor.Modified.UtcDateTime, TimeSpan.Zero);
        var buffer = new byte[(int)Math.Min(ChunkSize, Math.Max(4096, size - cursor.Offset))];
        var filled = 0;
        var position = cursor.Offset;
        stream.Seek(position, SeekOrigin.Begin);
        while (position + filled < size)
        {
            cancel.ThrowIfCancellationRequested();
            if (filled == buffer.Length)
            {
                // A line longer than the buffer: grow up to the maximum, then give up on it.
                if (buffer.Length >= MaxLineBytes)
                {
                    cursor.Discarding = true;
                    position += filled;
                    filled = 0;
                }
                else
                {
                    Array.Resize(ref buffer, Math.Min(MaxLineBytes, buffer.Length * 2));
                }
            }

            var want = (int)Math.Min(buffer.Length - filled, size - position - filled);
            var read = stream.Read(buffer, filled, want);
            if (read <= 0)
            {
                break;
            }

            filled += read;
            var start = 0;
            while (true)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n', start, filled - start);
                if (newline < 0)
                {
                    break;
                }

                if (cursor.Discarding)
                {
                    cursor.Discarding = false;
                }
                else
                {
                    var length = newline - start;
                    if (length > 0 && buffer[newline - 1] == '\r')
                    {
                        length--;
                    }

                    if (length > 0)
                    {
                        // Parsers finish with the line before returning, so the buffer slice is enough.
                        parser.Parse(buffer.AsMemory(start, length), fileTime, output);
                    }
                }

                start = newline + 1;
            }

            if (start > 0)
            {
                position += start;
                Buffer.BlockCopy(buffer, start, buffer, 0, filled - start);
                filled -= start;
            }

            if (cursor.Discarding && filled > 0)
            {
                // Still inside the oversized line: drop what was read of it.
                position += filled;
                filled = 0;
            }
        }

        // A trailing partial line stays unread until its newline arrives.
        cursor.Offset = position;
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Rivet.Core.Maintenance.Cleaner;

/// <summary>What a shortcut (.lnk) points at.</summary>
public sealed record ShellLinkTarget
{
    /// <summary>Target path (environment variables unexpanded), or null when the link has no file target.</summary>
    public string? Path { get; init; }

    public string? Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>An advertised Windows Installer shortcut (Darwin id): it names a product, not a file.</summary>
    public bool IsAdvertised { get; init; }

    /// <summary>The target is on a network share.</summary>
    public bool IsNetwork { get; init; }

    /// <summary>The path came from an ANSI field and holds non-ASCII text: it may be decoded wrongly, so it proves nothing.</summary>
    public bool IsUncertain { get; init; }
}

/// <summary>
/// Minimal reader for the Shell Link binary format ([MS-SHLLINK]): header,
/// optional ID list (skipped), LinkInfo, string data and the environment
/// variable block. Pure managed code so shortcut evidence is unit tested; a
/// link it cannot read yields null and never counts as broken.
/// </summary>
public static class ShellLink
{
    private const uint HasLinkTargetIdList = 0x1;
    private const uint HasLinkInfo = 0x2;
    private const uint HasName = 0x4;
    private const uint HasRelativePath = 0x8;
    private const uint HasWorkingDir = 0x10;
    private const uint HasArguments = 0x20;
    private const uint HasIconLocation = 0x40;
    private const uint IsUnicode = 0x80;
    private const uint HasDarwinId = 0x1000;
    private const uint EnvironmentBlockSignature = 0xA0000001;

    private static readonly byte[] LinkClsid = [0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];

    public static ShellLinkTarget? Read(ReadOnlySpan<byte> data)
    {
        try
        {
            return ReadCore(data);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    private static ShellLinkTarget? ReadCore(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x4C || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x4C || !data.Slice(4, 16).SequenceEqual(LinkClsid))
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        var offset = 0x4C;
        if ((flags & HasLinkTargetIdList) != 0)
        {
            var idListSize = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            offset += 2 + idListSize;
        }

        string? localPath = null;
        var uncertain = false;
        var network = false;
        if ((flags & HasLinkInfo) != 0)
        {
            var info = data[offset..];
            var infoSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(info);
            var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
            var infoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
            if ((infoFlags & 0x1) != 0)
            {
                var baseOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[16..]);
                var suffixOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[24..]);
                if (headerSize >= 0x24)
                {
                    var unicodeBase = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[28..]);
                    var unicodeSuffix = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[32..]);
                    if (unicodeBase > 0)
                    {
                        localPath = ReadUnicodeZ(info, unicodeBase) + (unicodeSuffix > 0 ? ReadUnicodeZ(info, unicodeSuffix) : string.Empty);
                    }
                }

                if (localPath is null && baseOffset > 0)
                {
                    var ansi = ReadAnsiZ(info, baseOffset) + (suffixOffset > 0 ? ReadAnsiZ(info, suffixOffset) : string.Empty);
                    uncertain = ansi.Any(c => c > 0x7F);
                    localPath = ansi;
                }
            }
            else if ((infoFlags & 0x2) != 0)
            {
                network = true;
            }

            offset += infoSize;
        }

        var unicode = (flags & IsUnicode) != 0;
        string? relative = null;
        string? workingDir = null;
        string? arguments = null;
        if ((flags & HasName) != 0)
        {
            ReadStringData(data, ref offset, unicode);
        }

        if ((flags & HasRelativePath) != 0)
        {
            relative = ReadStringData(data, ref offset, unicode);
        }

        if ((flags & HasWorkingDir) != 0)
        {
            workingDir = ReadStringData(data, ref offset, unicode);
        }

        if ((flags & HasArguments) != 0)
        {
            arguments = ReadStringData(data, ref offset, unicode);
        }

        if ((flags & HasIconLocation) != 0)
        {
            ReadStringData(data, ref offset, unicode);
        }

        string? environmentTarget = null;
        while (offset + 8 <= data.Length)
        {
            var blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            if (blockSize < 4)
            {
                break;
            }

            var signature = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            if (signature == EnvironmentBlockSignature && blockSize >= 8 + 260 + 520 && offset + blockSize <= data.Length)
            {
                environmentTarget = ReadUnicodeZ(data.Slice(offset + 8 + 260, 520), 0);
                if (environmentTarget.Length == 0)
                {
                    environmentTarget = null;
                }
            }

            offset += blockSize;
        }

        var advertised = (flags & HasDarwinId) != 0;
        var path = environmentTarget ?? (string.IsNullOrEmpty(localPath) ? null : localPath);
        if (path is null && relative is not null && !network)
        {
            // A relative path alone cannot be resolved without the link's own folder.
            path = null;
        }

        return new ShellLinkTarget
        {
            Path = advertised ? null : path,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            IsAdvertised = advertised,
            IsNetwork = network,
            IsUncertain = environmentTarget is null && uncertain,
        };
    }

    private static string ReadStringData(ReadOnlySpan<byte> data, ref int offset, bool unicode)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        var byteCount = unicode ? count * 2 : count;
        var text = unicode
            ? Encoding.Unicode.GetString(data.Slice(offset, byteCount))
            : Encoding.Latin1.GetString(data.Slice(offset, byteCount));
        offset += byteCount;
        return text;
    }

    private static string ReadUnicodeZ(ReadOnlySpan<byte> data, int offset)
    {
        var builder = new StringBuilder();
        for (var i = offset; i + 1 < data.Length; i += 2)
        {
            var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
            if (c == '\0')
            {
                break;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string ReadAnsiZ(ReadOnlySpan<byte> data, int offset)
    {
        var end = data[offset..].IndexOf((byte)0);
        var slice = end < 0 ? data[offset..] : data.Slice(offset, end);
        return Encoding.Latin1.GetString(slice);
    }
}

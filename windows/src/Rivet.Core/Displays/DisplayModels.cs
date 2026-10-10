// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Displays;

/// <summary>How a display's brightness is changed.</summary>
public enum BrightnessRoute
{
    /// <summary>Listed only (no way to change it).</summary>
    None,

    /// <summary>The OS controls the panel (laptop screens through WMI).</summary>
    System,

    /// <summary>DDC/CI: the monitor's own brightness, like its buttons.</summary>
    Ddc,

    /// <summary>A dark overlay dims the picture.</summary>
    Software,
}

/// <summary>What the DDC/CI channel of a monitor turned out to support.</summary>
public enum DdcState
{
    /// <summary>Not a DDC display.</summary>
    None,

    /// <summary>No reply to a read yet; the first write decides between write-only and dead.</summary>
    Unknown,

    /// <summary>Reads and writes work.</summary>
    Live,

    /// <summary>Writes are accepted but reads never answer.</summary>
    WriteOnly,

    /// <summary>Writes are rejected; the display falls back to dimming the picture.</summary>
    Dead,
}

/// <summary>A display as the platform enumerates it. Mirrored outputs fold into their source.</summary>
public sealed record DisplayDevice
{
    /// <summary>The session id: the GDI device name (<c>\\.\DISPLAY1</c>), as <see cref="ScreenInfo.Id"/>.</summary>
    public required string Id { get; init; }

    /// <summary>The monitor's name ("DELL U2720Q"); empty when Windows has none.</summary>
    public required string Name { get; init; }

    /// <summary>Monitor and connection: stable across launches; keys the remembered choices.</summary>
    public required string PathKey { get; init; }

    /// <summary>The monitor alone (vendor and product): a remembered level never moves to another monitor.</summary>
    public required string Fingerprint { get; init; }

    public bool IsInternal { get; init; }

    public bool IsPrimary { get; init; }

    /// <summary>Monitor area in physical pixels.</summary>
    public PixelRect Bounds { get; init; }
}

/// <summary>One display as the UI shows it (an immutable snapshot).</summary>
public sealed record DisplayStatus
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string PathKey { get; init; }

    public bool IsInternal { get; init; }

    public bool IsPrimary { get; init; }

    public PixelRect Bounds { get; init; }

    public BrightnessRoute Route { get; init; }

    public DdcState Ddc { get; init; }

    /// <summary>The slider value, 0…1.</summary>
    public double Level { get; init; }

    public bool ForcedSoftware { get; init; }

    public bool ExtendedDimming { get; init; }

    /// <summary>A write failed; shown in red under the row until the next success.</summary>
    public string? Error { get; init; }

    public bool CanAdjust => Route != BrightnessRoute.None;

    /// <summary>"Dim the picture" is offered for monitors that accept writes but never confirm them.</summary>
    public bool OffersForcedSoftware => ForcedSoftware || (Route == BrightnessRoute.Ddc && Ddc is DdcState.WriteOnly or DdcState.Unknown);

    /// <summary>"Extra dimming" is offered for monitors whose level can be read.</summary>
    public bool OffersExtendedDimming => Route == BrightnessRoute.Ddc && Ddc == DdcState.Live;

    public int Percent => (int)Math.Round(Math.Clamp(Level, 0, 1) * 100, MidpointRounding.AwayFromZero);
}

public readonly record struct DdcReading(int Current, int Maximum);

/// <summary>Active displays and the events that should rebuild the list.</summary>
public interface IDisplayCatalog
{
    /// <summary>Active displays; may take tens of milliseconds, so call it off the UI thread.</summary>
    IReadOnlyList<DisplayDevice> Enumerate();

    /// <summary>The display configuration changed (connect, disconnect, rearrange, resolution).</summary>
    event EventHandler? ConfigurationChanged;

    /// <summary>The PC woke from sleep (monitors need a few seconds before DDC answers).</summary>
    event EventHandler? Resumed;
}

/// <summary>Brightness the OS controls itself (laptop panels through WMI on Windows).</summary>
public interface ISystemBrightness
{
    /// <summary>The current level 0–100, or null when the OS does not control this display.</summary>
    int? Read(DisplayDevice display);

    bool Write(DisplayDevice display, int percent);
}

/// <summary>DDC/CI luminance (VCP 0x10). Calls are slow; the service calls them on its worker only.</summary>
public interface IDdcChannel
{
    /// <summary>Opens the monitor handles for this set of displays (each rebuild), closing older ones.</summary>
    void Open(IReadOnlyList<DisplayDevice> displays);

    /// <summary>The display has a monitor handle that DDC commands can be sent to.</summary>
    bool HasChannel(DisplayDevice display);

    /// <summary>Reads luminance; null when the monitor does not answer.</summary>
    DdcReading? Read(DisplayDevice display);

    /// <summary>Sets luminance in device units (0…maximum); false when the write is rejected.</summary>
    bool Write(DisplayDevice display, int value);

    void Close();
}

/// <summary>Dims a display's picture (the app draws a dark overlay). Called on the UI thread.</summary>
public interface ISoftwareDimmer
{
    /// <summary>Dims to <paramref name="factor"/> of the normal picture (1 = no dimming).</summary>
    void Apply(DisplayDevice display, double factor);

    void Remove(string displayId);

    void RemoveAll();
}

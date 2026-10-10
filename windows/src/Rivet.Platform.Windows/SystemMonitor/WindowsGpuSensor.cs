// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.SystemMonitor;
using Vortice.DXGI;
using Windows.Win32;
using Windows.Win32.System.Performance;

namespace Rivet.Platform.Windows.SystemMonitor;

/// <summary>A PDH query with wildcard counters, collected together; values come back per instance.</summary>
internal sealed unsafe class PdhCounterSet : IDisposable
{
    private readonly PdhCloseQuerySafeHandle _query;
    private readonly Dictionary<string, PDH_HCOUNTER> _counters = new(StringComparer.Ordinal);

    private PdhCounterSet(PdhCloseQuerySafeHandle query) => _query = query;

    public static PdhCounterSet? Open(params string[] paths)
    {
        if (PInvoke.PdhOpenQuery((string?)null, 0, out var query) != 0)
        {
            return null;
        }

        var set = new PdhCounterSet(query);
        foreach (var path in paths)
        {
            if (PInvoke.PdhAddEnglishCounter(query, path, 0, out var counter) == 0)
            {
                set._counters[path] = counter;
            }
        }

        if (set._counters.Count == 0)
        {
            set.Dispose();
            return null;
        }

        return set;
    }

    public bool Collect() => PInvoke.PdhCollectQueryData(new PDH_HQUERY(_query.DangerousGetHandle())) == 0;

    /// <summary>Formatted values of every instance of one counter (empty until two collections exist).</summary>
    public List<(string Instance, double Value)> Values(string path)
    {
        var result = new List<(string, double)>();
        if (!_counters.TryGetValue(path, out var counter))
        {
            return result;
        }

        uint size = 0;
        uint count = 0;
        var format = PDH_FMT.PDH_FMT_DOUBLE | (PDH_FMT)0x00008000; // PDH_FMT_NOCAP100
        var status = PInvoke.PdhGetFormattedCounterArray(counter, format, &size, &count, null);
        if (status != PInvoke.PDH_MORE_DATA || size == 0)
        {
            return result;
        }

        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            var items = (PDH_FMT_COUNTERVALUE_ITEM_W*)p;
            if (PInvoke.PdhGetFormattedCounterArray(counter, format, &size, &count, items) != 0)
            {
                return result;
            }

            for (var i = 0; i < count; i++)
            {
                var value = items[i].FmtValue;
                if (value.CStatus is not (PInvoke.PDH_CSTATUS_VALID_DATA or 1u /* PDH_CSTATUS_NEW_DATA */))
                {
                    continue;
                }

                result.Add((items[i].szName.ToString(), value.Anonymous.doubleValue));
            }
        }

        return result;
    }

    public void Dispose() => _query.Dispose();
}

/// <summary>
/// GPU usage from PDH "GPU Engine(*)\Utilization Percentage" with Task
/// Manager semantics: per adapter, sum the processes on each engine and take
/// the busiest engine; the overall figure is the busiest adapter. Dedicated
/// and shared memory from "GPU Adapter Memory(*)". Adapter names and sizes
/// from DXGI. Temperature from the vendor interfaces (<see cref="HardwareSensors"/>),
/// otherwise through D3DKMTQueryAdapterInfo(ADAPTERPERFDATA) where the
/// driver reports it to Windows (WDDM 2.4+, usually discrete GPUs).
/// </summary>
public sealed unsafe partial class WindowsGpuSensor : IGpuSensor, IDisposable
{
    private const string EnginePath = @"\GPU Engine(*)\Utilization Percentage";
    private const string DedicatedPath = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string SharedPath = @"\GPU Adapter Memory(*)\Shared Usage";

    private readonly object _gate = new();
    private PdhCounterSet? _pdh;
    private bool _pdhFailed;
    private Dictionary<string, (string Name, ulong Dedicated, uint Low, int High)>? _adapters;
    private bool _temperatureUnsupported;
    private readonly HardwareSensors _hardware;

    public WindowsGpuSensor(HardwareSensors hardware) => _hardware = hardware;

    public GpuSample? ReadUsage()
    {
        lock (_gate)
        {
            if (_pdhFailed)
            {
                return null;
            }

            _pdh ??= PdhCounterSet.Open(EnginePath, DedicatedPath, SharedPath);
            if (_pdh is null)
            {
                _pdhFailed = true;
                Log.Info("monitor", "GPU performance counters are not available on this PC.");
                return null;
            }

            if (!_pdh.Collect())
            {
                return null;
            }

            var engines = new Dictionary<(string Luid, string Engine), double>();
            var perProcess = new Dictionary<int, double>();
            foreach (var (instance, value) in _pdh.Values(EnginePath))
            {
                if (!TryParseEngine(instance, out var pid, out var luid, out var engine))
                {
                    continue;
                }

                var key = (luid, engine);
                engines[key] = engines.GetValueOrDefault(key) + value;
                perProcess[pid] = Math.Max(perProcess.GetValueOrDefault(pid), value);
            }

            if (engines.Count == 0)
            {
                // First collection of a rate counter has no data yet.
                return null;
            }

            var dedicated = MemoryByAdapter(_pdh.Values(DedicatedPath));
            var shared = MemoryByAdapter(_pdh.Values(SharedPath));
            var names = Adapters();
            var adapters = engines
                .GroupBy(e => e.Key.Luid)
                .Select(g =>
                {
                    names.TryGetValue(g.Key, out var info);
                    return new GpuAdapterReading
                    {
                        Id = g.Key,
                        Name = info.Name ?? g.Key,
                        Usage = Math.Clamp(g.Max(e => e.Value) / 100.0, 0, 1),
                        DedicatedUsed = dedicated.TryGetValue(g.Key, out var d) ? d : null,
                        DedicatedTotal = info.Dedicated > 0 ? info.Dedicated : null,
                        SharedUsed = shared.TryGetValue(g.Key, out var s) ? s : null,
                    };
                })
                // Hide adapters DXGI does not list as hardware (Basic Render Driver, virtual ones).
                .Where(a => names.Count == 0 || names.ContainsKey(a.Id))
                .OrderByDescending(a => a.DedicatedTotal ?? 0)
                .ToList();

            return new GpuSample
            {
                Usage = adapters.Count == 0 ? 0 : adapters.Max(a => a.Usage),
                Adapters = adapters,
                ProcessUsage = perProcess,
            };
        }
    }

    public double? ReadTemperature()
    {
        if (_hardware.ReadGpuTemperature() is { } vendor)
        {
            return vendor;
        }

        lock (_gate)
        {
            if (_temperatureUnsupported)
            {
                return null;
            }

            double? hottest = null;
            foreach (var (_, info) in Adapters())
            {
                if (QueryTemperature(info.Low, info.High) is { } t && (hottest is null || t > hottest))
                {
                    hottest = t;
                }
            }

            if (hottest is null)
            {
                // No driver reports it; do not keep asking every tick.
                _temperatureUnsupported = true;
            }

            return hottest;
        }
    }

    /// <summary>"pid_1234_luid_0x00000000_0x0000D1A6_phys_0_eng_3_engtype_3D".</summary>
    internal static bool TryParseEngine(string instance, out int pid, out string luid, out string engine)
    {
        pid = 0;
        luid = string.Empty;
        engine = string.Empty;
        var parts = instance.Split('_');
        var pidIndex = Array.IndexOf(parts, "pid");
        var luidIndex = Array.IndexOf(parts, "luid");
        var engIndex = Array.IndexOf(parts, "eng");
        if (pidIndex < 0 || luidIndex < 0 || engIndex < 0 || luidIndex + 2 >= parts.Length || engIndex + 1 >= parts.Length
            || !int.TryParse(parts[pidIndex + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
        {
            return false;
        }

        luid = $"{parts[luidIndex + 1]}_{parts[luidIndex + 2]}".ToUpperInvariant().Replace("0X", "0x", StringComparison.Ordinal);
        engine = parts[engIndex + 1];
        return true;
    }

    private static Dictionary<string, ulong> MemoryByAdapter(List<(string Instance, double Value)> values)
    {
        var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var (instance, value) in values)
        {
            var parts = instance.Split('_');
            var luidIndex = Array.IndexOf(parts, "luid");
            if (luidIndex < 0 || luidIndex + 2 >= parts.Length)
            {
                continue;
            }

            var key = $"{parts[luidIndex + 1]}_{parts[luidIndex + 2]}".ToUpperInvariant().Replace("0X", "0x", StringComparison.Ordinal);
            result[key] = result.GetValueOrDefault(key) + (ulong)Math.Max(0, value);
        }

        return result;
    }

    /// <summary>Hardware adapters by PDH luid key ("0x00000000_0x0000D1A6"), from DXGI.</summary>
    private Dictionary<string, (string Name, ulong Dedicated, uint Low, int High)> Adapters()
    {
        if (_adapters is not null)
        {
            return _adapters;
        }

        var result = new Dictionary<string, (string, ulong, uint, int)>(StringComparer.Ordinal);
        try
        {
            if (DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).Success && factory is not null)
            {
                using (factory)
                {
                    for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success && adapter is not null; i++)
                    {
                        using (adapter)
                        {
                            var desc = adapter.Description1;
                            if ((desc.Flags & AdapterFlags.Software) != 0)
                            {
                                continue;
                            }

                            var low = desc.Luid.LowPart;
                            var high = desc.Luid.HighPart;
                            var key = $"0x{high:X8}_0x{low:X8}";
                            result[key] = (desc.Description.Trim(), (ulong)desc.DedicatedVideoMemory, low, high);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("monitor", "Could not list graphics adapters.", ex);
        }

        _adapters = result;
        return result;
    }

    // D3DKMT (gdi32): adapter performance data, including the temperature Task Manager shows.
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapterFromLuid
    {
        public Luid AdapterLuid;
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public void* PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterPerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PcieBandwidth;
        public uint FanRpm;
        public uint Power;

        /// <summary>Deci-degrees Celsius (1 = 0.1 °C).</summary>
        public uint Temperature;
        public byte PowerStateOverride;
    }

    private const int KmtqaiTypeAdapterPerfData = 62;

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTOpenAdapterFromLuid(OpenAdapterFromLuid* request);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTQueryAdapterInfo(QueryAdapterInfo* request);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTCloseAdapter(uint* adapter);

    private static double? QueryTemperature(uint low, int high)
    {
        try
        {
            var open = new OpenAdapterFromLuid { AdapterLuid = new Luid { LowPart = low, HighPart = high } };
            if (D3DKMTOpenAdapterFromLuid(&open) != 0)
            {
                return null;
            }

            try
            {
                var data = default(AdapterPerfData);
                var query = new QueryAdapterInfo
                {
                    Adapter = open.Adapter,
                    Type = KmtqaiTypeAdapterPerfData,
                    PrivateDriverData = &data,
                    PrivateDriverDataSize = (uint)sizeof(AdapterPerfData),
                };
                if (D3DKMTQueryAdapterInfo(&query) != 0 || data.Temperature == 0)
                {
                    return null;
                }

                var celsius = data.Temperature / 10.0;
                return celsius is > 1 and < 125 ? celsius : null;
            }
            finally
            {
                var handle = open.Adapter;
                D3DKMTCloseAdapter(&handle);
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pdh?.Dispose();
            _pdh = null;
        }
    }
}

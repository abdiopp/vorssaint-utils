// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Wmi;

namespace Rivet.Platform.Windows.SystemMonitor;

/// <summary>Why a WMI call failed, from its HRESULT.</summary>
internal enum WmiFailure
{
    None,
    AccessDenied,
    NotSupported,
    Other,
}

/// <summary>
/// A minimal WMI client on CsWin32's COM projections (System.Management is
/// not referenced). Connects lazily to one namespace, sets impersonation on
/// every proxy, and serializes calls. Calls are slow (tens of milliseconds):
/// use it only from background threads, which are MTA in .NET.
/// </summary>
internal sealed unsafe class WmiClient : IDisposable
{
    private const uint RpcAuthnWinNt = 10;
    private const uint RpcAuthzNone = 0;

    private readonly string _namespace;
    private readonly object _gate = new();
    private IWbemServices? _services;

    public WmiClient(string wmiNamespace) => _namespace = wmiNamespace;

    public static WmiFailure Classify(Exception ex) => ex switch
    {
        COMException com when (uint)com.HResult is 0x80041003 or 0x80070005 => WmiFailure.AccessDenied,
        COMException com when (uint)com.HResult is 0x80041010 or 0x80041002 or 0x8004100C or 0x80041013 => WmiFailure.NotSupported,
        UnauthorizedAccessException => WmiFailure.AccessDenied,
        _ => WmiFailure.Other,
    };

    /// <summary>Runs a WQL query and reads the named properties of every result.</summary>
    public List<Dictionary<string, object?>> Query(string wql, params string[] properties)
    {
        lock (_gate)
        {
            var services = Connect();
            using var language = new Bstr("WQL");
            using var query = new Bstr(wql);
            services.ExecQuery(language.Value, query.Value, WBEM_GENERIC_FLAG_TYPE.WBEM_FLAG_FORWARD_ONLY | WBEM_GENERIC_FLAG_TYPE.WBEM_FLAG_RETURN_IMMEDIATELY, null, out var enumerator);
            var rows = new List<Dictionary<string, object?>>();
            try
            {
                SetBlanket(enumerator);
                var buffer = new IWbemClassObject[1];
                while (true)
                {
                    var hr = enumerator.Next(5000, buffer, out var returned);
                    if (hr.Failed)
                    {
                        Marshal.ThrowExceptionForHR(hr.Value);
                    }

                    if (returned == 0 || buffer[0] is not { } item)
                    {
                        break;
                    }

                    try
                    {
                        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (var name in properties)
                        {
                            object? value = null;
                            item.Get(name, 0, ref value!);
                            row[name] = value is DBNull ? null : value;
                        }

                        rows.Add(row);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                        buffer[0] = null!;
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }

            return rows;
        }
    }

    /// <summary>Calls a method on one instance (e.g. WmiMonitorBrightnessMethods.WmiSetBrightness).</summary>
    public void Invoke(string objectPath, string className, string method, IReadOnlyList<(string Name, object Value)> arguments)
    {
        lock (_gate)
        {
            var services = Connect();
            using var classPath = new Bstr(className);
            IWbemClassObject_unmanaged* rawClass = null;
            services.GetObject(classPath.Value, 0, null, &rawClass, null);
            if (rawClass is null)
            {
                throw new COMException("WMI class not found.", unchecked((int)0x80041010));
            }

            var classObject = (IWbemClassObject)Marshal.GetObjectForIUnknown((nint)rawClass);
            Marshal.Release((nint)rawClass);
            IWbemClassObject? signature = null;
            IWbemClassObject? parameters = null;
            try
            {
                classObject.GetMethod(method, 0, out signature, out _);
                signature.SpawnInstance(0, out parameters);
                foreach (var (name, value) in arguments)
                {
                    parameters.Put(name, 0, value, 0);
                }

                using var path = new Bstr(objectPath);
                using var methodName = new Bstr(method);
                services.ExecMethod(path.Value, methodName.Value, 0, null, parameters, null, null);
            }
            finally
            {
                if (parameters is not null)
                {
                    Marshal.ReleaseComObject(parameters);
                }

                if (signature is not null)
                {
                    Marshal.ReleaseComObject(signature);
                }

                Marshal.ReleaseComObject(classObject);
            }
        }
    }

    private IWbemServices Connect()
    {
        if (_services is not null)
        {
            return _services;
        }

        var locator = (IWbemLocator)new WbemLocator();
        try
        {
            using var path = new Bstr(_namespace);
            locator.ConnectServer(path.Value, default, default, default, 0, default, null, out var services);
            SetBlanket(services);
            _services = services;
            return services;
        }
        finally
        {
            Marshal.ReleaseComObject(locator);
        }
    }

    private static void SetBlanket(object proxy) =>
        PInvoke.CoSetProxyBlanket(proxy, RpcAuthnWinNt, RpcAuthzNone, default, RPC_C_AUTHN_LEVEL.RPC_C_AUTHN_LEVEL_CALL,
            RPC_C_IMP_LEVEL.RPC_C_IMP_LEVEL_IMPERSONATE, null, EOLE_AUTHENTICATION_CAPABILITIES.EOAC_NONE);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_services is not null)
            {
                Marshal.ReleaseComObject(_services);
                _services = null;
            }
        }
    }

    /// <summary>A BSTR for the duration of one call.</summary>
    private readonly struct Bstr : IDisposable
    {
        private readonly nint _pointer;

        public Bstr(string text) => _pointer = Marshal.StringToBSTR(text);

        public BSTR Value => new((char*)_pointer);

        public void Dispose() => Marshal.FreeBSTR(_pointer);
    }
}

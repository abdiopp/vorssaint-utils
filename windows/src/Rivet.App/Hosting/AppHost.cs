// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Modules;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Hosting;

/// <summary>
/// Composition root: settings, localization, the feature runtime, the
/// platform services (found by reflection in the platform assembly) and the
/// feature modules (found by reflection in this assembly).
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly List<IFeatureModule> _modules;
    private bool _started;

    private AppHost(AppPaths paths, SettingsStore settings, ServiceProvider services, List<IFeatureModule> modules)
    {
        Paths = paths;
        Settings = settings;
        Services = services;
        _modules = modules;
    }

    public static AppHost? Current { get; private set; }

    public AppPaths Paths { get; }

    public SettingsStore Settings { get; }

    public ServiceProvider Services { get; }

    public IReadOnlyList<IFeatureModule> Modules => _modules;

    /// <summary>The platform assembly for this build: Windows for the Windows target, fakes elsewhere.</summary>
    public static Assembly DefaultPlatformAssembly =>
#if WINDOWS_PLATFORM
        typeof(Rivet.Platform.Windows.WindowsShellRegistrar).Assembly;
#else
        typeof(Rivet.Platform.Fake.FakeShellRegistrar).Assembly;
#endif

    public static AppHost Create(
        AppPaths paths,
        SettingsStore? settings = null,
        Assembly? platformAssembly = null,
        Func<Type, bool>? moduleFilter = null)
    {
        settings ??= SettingsStore.Load(paths.SettingsFile);
        SettingsMigrations.Run(settings);

        var language = ResolveLanguage(settings);
        Localizer.Current = new Localizer(language);
        CultureInfo.CurrentUICulture = language.Culture();

        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(settings);
        services.AddSingleton<ISettingsStore>(settings);
        services.AddSingleton(_ => Localizer.Current);
        services.AddSingleton<FeatureRuntime>();
        services.AddSingleton<ActionRegistry>();
        services.AddSingleton<ShortcutManager>();
        services.AddSingleton<PanelRegistry>();
        services.AddSingleton<SettingsPageRegistry>();
        services.AddSingleton<TrayMenuRegistry>();
        services.AddSingleton<SearchProviderRegistry>();

        foreach (var registrar in Discover<IPlatformRegistrar>(platformAssembly ?? DefaultPlatformAssembly).OrderBy(r => r.Order))
        {
            registrar.Register(services);
        }

        var modules = Discover<IFeatureModule>(typeof(AppHost).Assembly, moduleFilter).OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        foreach (var module in modules)
        {
            try
            {
                module.ConfigureServices(services);
            }
            catch (Exception ex)
            {
                Log.Error("host", $"Module '{module.Id}' failed to configure services.", ex);
            }
        }

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = false });
        var host = new AppHost(paths, settings, provider, modules);
        Current = host;
        return host;
    }

    /// <summary>Initializes modules and starts installed features. Call on the UI thread.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        var runtime = Services.GetRequiredService<FeatureRuntime>();
        runtime.PrepareFirstRunAvailability();

        var context = new ModuleContext(Services);
        foreach (var module in _modules)
        {
            try
            {
                module.Initialize(context);
            }
            catch (Exception ex)
            {
                Log.Error("host", $"Module '{module.Id}' failed to initialize.", ex);
            }
        }

        runtime.SyncAtLaunch();
        Services.GetRequiredService<ShortcutManager>().SyncAll();
        Log.Info("host", $"Started {AppIdentity.DisplayName} {AppIdentity.VersionString} with {_modules.Count} modules, {runtime.AvailableCount} features installed.");
    }

    public static AppLanguage ResolveLanguage(ISettingsStore settings)
    {
        var stored = settings.Get(ShellSettings.AppLanguage);
        if (AppLanguages.TryParseCode(stored, out var language) && stored.Length > 0)
        {
            return language;
        }

        return AppLanguages.FromPreferredLanguages([CultureInfo.CurrentUICulture.Name]);
    }

    private static IEnumerable<T> Discover<T>(Assembly assembly, Func<Type, bool>? filter = null)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.OfType<Type>().ToArray();
        }

        foreach (var type in types.Where(t => typeof(T).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }))
        {
            if (filter is not null && !filter(type))
            {
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                Log.Warn("host", $"{type.FullName} needs a parameterless constructor to be discovered.");
                continue;
            }

            T instance;
            try
            {
                instance = (T)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                Log.Error("host", $"Could not create {type.FullName}.", ex);
                continue;
            }

            yield return instance;
        }
    }

    public void Dispose()
    {
        try
        {
            Services.GetService<ShortcutManager>()?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("host", "Shortcut shutdown failed.", ex);
        }

        // Disposing the container disposes every singleton in reverse creation
        // order: services that changed system state restore it here.
        try
        {
            Services.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("host", "Service shutdown failed.", ex);
        }

        Settings.Dispose();
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Rivet.Core.Diagnostics;
using Rivet.Core.Util;

namespace Rivet.Core.Localization;

/// <summary>
/// The UI string catalog. Keys are the dotted ids extracted from the macOS
/// catalogs (<c>Strings.menuSettings</c>, <c>screenshot.pageTitle</c>, ...)
/// plus Windows-specific keys (<c>win.&lt;module&gt;.&lt;key&gt;</c>).
///
/// Lookup order for language L: Windows strings in L, macOS strings in L,
/// Windows strings in en-US, macOS strings in en-US, and finally the key
/// itself (logged once). A Windows rewording that exists only in English
/// therefore does not replace an existing translation.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    private const string IndexerName = "Item[]";

    private readonly ConcurrentDictionary<AppLanguage, IReadOnlyDictionary<string, string>> _mac = new();
    private readonly ConcurrentDictionary<AppLanguage, IReadOnlyDictionary<string, string>> _win = new();
    private readonly ConcurrentDictionary<string, byte> _reportedMissing = new(StringComparer.Ordinal);
    private readonly Func<AppLanguage, IReadOnlyDictionary<string, string>> _loadMac;
    private readonly Func<AppLanguage, IReadOnlyDictionary<string, string>> _loadWin;
    private readonly List<WeakReference<LocalizedObservable>> _observables = [];
    private AppLanguage _language;

    public Localizer(AppLanguage language)
        : this(language, LoadEmbeddedMac, LoadEmbeddedWindows)
    {
    }

    /// <summary>Test seam: supply the catalogs directly.</summary>
    public Localizer(
        AppLanguage language,
        Func<AppLanguage, IReadOnlyDictionary<string, string>> loadMac,
        Func<AppLanguage, IReadOnlyDictionary<string, string>> loadWin)
    {
        _language = language;
        _loadMac = loadMac;
        _loadWin = loadWin;
    }

    /// <summary>The process-wide instance; the app replaces it at startup.</summary>
    public static Localizer Current { get; set; } = new(AppLanguage.EnUS);

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public AppLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value)
            {
                return;
            }

            _language = value;
            CultureInfo.CurrentUICulture = value.Culture();
            UiThread.Run(() =>
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(IndexerName));
                LanguageChanged?.Invoke(this, EventArgs.Empty);
                NotifyObservables();
            });
        }
    }

    public CultureInfo Culture => _language.Culture();

    public PluralForm PluralFormFor(long count) => _language.CountAgreement().FormFor(count);

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (TryGet(key, out var value))
        {
            return value;
        }

        if (_reportedMissing.TryAdd(key, 0))
        {
            Log.Warn("l10n", $"Missing string '{key}'.");
        }

        return key;
    }

    public bool TryGet(string key, out string value)
    {
        var language = _language;
        if (Win(language).TryGetValue(key, out value!) || Mac(language).TryGetValue(key, out value!))
        {
            return true;
        }

        if (language != AppLanguage.EnUS &&
            (Win(AppLanguage.EnUS).TryGetValue(key, out value!) || Mac(AppLanguage.EnUS).TryGetValue(key, out value!)))
        {
            return true;
        }

        value = key;
        return false;
    }

    /// <summary>Looks up a printf-style format string and fills it in.</summary>
    public string Format(string key, params object?[] args) =>
        PrintfFormatter.Format(Get(key), Culture, args);

    /// <summary>Picks one of three keys by the language's count agreement, then formats with <paramref name="count"/>.</summary>
    public string Plural(long count, string oneKey, string fewKey, string manyKey)
    {
        var key = PluralFormFor(count) switch
        {
            PluralForm.One => oneKey,
            PluralForm.Few => fewKey,
            _ => manyKey,
        };
        return PrintfFormatter.Format(Get(key), Culture, count);
    }

    /// <summary>An observable that pushes the string now and after every language change.</summary>
    public IObservable<string> Observe(string key)
    {
        var observable = new LocalizedObservable(this, key);
        lock (_observables)
        {
            _observables.RemoveAll(w => !w.TryGetTarget(out _));
            _observables.Add(new WeakReference<LocalizedObservable>(observable));
        }

        return observable;
    }

    /// <summary>Every key known in English (both catalogs).</summary>
    public IEnumerable<string> AllKeys() =>
        Mac(AppLanguage.EnUS).Keys.Concat(Win(AppLanguage.EnUS).Keys).Distinct(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, string> Mac(AppLanguage language) => _mac.GetOrAdd(language, _loadMac);

    private IReadOnlyDictionary<string, string> Win(AppLanguage language) => _win.GetOrAdd(language, _loadWin);

    private void NotifyObservables()
    {
        LocalizedObservable[] live;
        lock (_observables)
        {
            live = _observables.Select(w => w.TryGetTarget(out var o) ? o : null).OfType<LocalizedObservable>().ToArray();
        }

        foreach (var observable in live)
        {
            observable.Push();
        }
    }

    /// <summary>The macOS brand appears in many extracted strings; the Windows build shows its own name.</summary>
    private const string SourceBrand = "Vorssaint";

    private static IReadOnlyDictionary<string, string> LoadEmbeddedMac(AppLanguage language) =>
        Rebrand(ReadJsonResource(typeof(Localizer).Assembly, $"i18n/{language.Code()}.json"));

    private static IReadOnlyDictionary<string, string> Rebrand(IReadOnlyDictionary<string, string> strings)
    {
        var name = App.AppIdentity.DisplayName;
        if (name == SourceBrand)
        {
            return strings;
        }

        var result = new Dictionary<string, string>(strings.Count, StringComparer.Ordinal);
        foreach (var (key, value) in strings)
        {
            result[key] = value.Contains(SourceBrand, StringComparison.Ordinal)
                ? value.Replace(SourceBrand, name, StringComparison.Ordinal)
                : value;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> LoadEmbeddedWindows(AppLanguage language)
    {
        var assembly = typeof(Localizer).Assembly;
        var suffix = $".{language.Code()}.json";
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith("i18n-win/", StringComparison.Ordinal) && n.EndsWith(suffix, StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            foreach (var (key, value) in ReadJsonResource(assembly, name))
            {
                if (!merged.TryAdd(key, value))
                {
                    Log.Warn("l10n", $"Duplicate Windows string '{key}' in {name}.");
                }
            }
        }

        return Rebrand(merged);
    }

    private static IReadOnlyDictionary<string, string> ReadJsonResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
        }
        catch (JsonException ex)
        {
            Log.Error("l10n", $"Unreadable string resource {name}.", ex);
            return new Dictionary<string, string>();
        }
    }

    private sealed class LocalizedObservable(Localizer owner, string key) : IObservable<string>
    {
        private readonly List<IObserver<string>> _observers = [];

        public IDisposable Subscribe(IObserver<string> observer)
        {
            lock (_observers)
            {
                _observers.Add(observer);
            }

            observer.OnNext(owner.Get(key));
            return new Unsubscriber(this, observer);
        }

        public void Push()
        {
            IObserver<string>[] observers;
            lock (_observers)
            {
                observers = _observers.ToArray();
            }

            var value = owner.Get(key);
            foreach (var observer in observers)
            {
                observer.OnNext(value);
            }
        }

        private sealed class Unsubscriber(LocalizedObservable owner, IObserver<string> observer) : IDisposable
        {
            public void Dispose()
            {
                lock (owner._observers)
                {
                    owner._observers.Remove(observer);
                }
            }
        }
    }
}

/// <summary>Short alias used throughout the code: <c>L.Get("screenshot.pageTitle")</c>.</summary>
public static class L
{
    public static string Get(string key) => Localizer.Current.Get(key);

    public static string Format(string key, params object?[] args) => Localizer.Current.Format(key, args);

    public static string Plural(long count, string oneKey, string fewKey, string manyKey) =>
        Localizer.Current.Plural(count, oneKey, fewKey, manyKey);
}

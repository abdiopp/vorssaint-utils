// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Data;
using Rivet.Core.Localization;

namespace Rivet.App.Localization;

/// <summary>
/// <c>Text="{l:Tr screenshot.pageTitle}"</c>: a binding that follows the
/// current UI language, so switching languages updates every open window.
/// </summary>
public sealed class TrExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    /// <summary>Optional: uppercase the result (section titles).</summary>
    public bool Upper { get; set; }

    public BindingBase ProvideValue(IServiceProvider serviceProvider)
    {
        var observable = Localizer.Current.Observe(Key);
        return Upper ? new UpperObservable(observable).ToBinding() : observable.ToBinding();
    }

    private sealed class UpperObservable(IObservable<string> inner) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer) =>
            inner.Subscribe(new Upper(observer));

        private sealed class Upper(IObserver<string> target) : IObserver<string>
        {
            public void OnCompleted() => target.OnCompleted();

            public void OnError(Exception error) => target.OnError(error);

            public void OnNext(string value) => target.OnNext(value.ToUpper(Localizer.Current.Culture));
        }
    }
}

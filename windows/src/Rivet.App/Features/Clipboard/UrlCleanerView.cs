// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Clipboard;
using Rivet.Core.Localization;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Clipboard;

/// <summary>
/// The manual URL cleaner (spec 06 §3.5.3): a field, Paste and Copy, the
/// cleaned link and the shared outcome message, recomputed on every change
/// with the rules in force. The panel variant also carries the automatic switch.
/// </summary>
public sealed class UrlCleanerView : UserControl
{
    private readonly UrlCleanerService _cleaner;
    private readonly TextBox _input = new();
    private readonly SelectableTextBlock _output = new() { Classes = { "mono" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _message = new() { Classes = { "caption" } };
    private readonly Button _copy;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _justCopied;

    public UrlCleanerView(IServiceProvider services, bool showAutomaticSwitch, bool showTitle = true)
    {
        _cleaner = services.GetRequiredService<UrlCleanerService>();
        var settings = services.GetRequiredService<ISettingsStore>();

        _input.PlaceholderText = L.Get("Strings.urlCleanerInputPlaceholder");
        AutomationProperties.SetName(_input, L.Get("Strings.urlCleanerInputPlaceholder"));
        _input.TextChanged += (_, _) =>
        {
            _justCopied = false;
            Recompute();
        };
        var clearField = ClipboardUi.IconButton("Dismiss", L.Get("Strings.urlCleanerClearButton"), () => _input.Text = string.Empty, 12);
        _input.InnerRightContent = clearField;

        var paste = new Button { Content = L.Get("Strings.urlCleanerPasteButton") };
        paste.Click += async (_, _) =>
        {
            var text = await _cleaner.ReadClipboardTextAsync().ConfigureAwait(true);
            if (text is not null)
            {
                _input.Text = text.Trim();
            }
        };
        _copy = new Button { Content = L.Get("Strings.urlCleanerCopyButton"), Classes = { "accent" }, IsEnabled = false };
        _copy.Click += async (_, _) =>
        {
            var outcome = _cleaner.Evaluate(_input.Text ?? string.Empty);
            if (outcome.Url is { } url && await _cleaner.CopyAsync(url).ConfigureAwait(true))
            {
                _justCopied = true;
                Recompute();
            }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { paste, _copy } };
        var resultCard = new Border { Classes = { "card" }, Child = new StackPanel { Spacing = 4, Children = { _output, _message } } };
        var stack = new StackPanel { Spacing = 8 };
        if (showAutomaticSwitch)
        {
            var enabled = settings.Bind(ClipboardSettings.UrlCleanerEnabled);
            _subscriptions.Add(enabled);
            var automatic = new CheckBox { Content = L.Get("Strings.urlCleanerEnable"), IsChecked = enabled.Value };
            var active = new TextBlock { Text = "● " + L.Get("Strings.urlCleanerActiveNow"), FontSize = 11, IsVisible = enabled.Value };
            active.Bind(TextBlock.ForegroundProperty, active.GetResourceObservable("SuccessBrush").ToBinding());
            automatic.IsCheckedChanged += (_, _) => enabled.Value = automatic.IsChecked == true;
            enabled.PropertyChanged += (_, _) =>
            {
                automatic.IsChecked = enabled.Value;
                active.IsVisible = enabled.Value;
            };
            stack.Children.Add(new Border
            {
                Classes = { "card" },
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children = { automatic, new TextBlock { Text = L.Get("Strings.urlCleanerEnableCaption"), Classes = { "caption" } }, active },
                },
            });
        }

        if (showTitle)
        {
            stack.Children.Add(new TextBlock { Text = L.Get("Strings.urlCleanerManualTitle").ToUpper(Localizer.Current.Culture), Classes = { "sectionTitle" }, Margin = new Thickness(4, 0) });
        }

        stack.Children.Add(_input);
        stack.Children.Add(buttons);
        stack.Children.Add(resultCard);
        Content = stack;
        _cleaner.RulesChanged += OnRulesChanged;
        Recompute();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cleaner.RulesChanged -= OnRulesChanged;
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Pre-fills the field (Settings deep links, tests).</summary>
    public void SetInput(string text) => _input.Text = text;

    private void OnRulesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Recompute);

    private void Recompute()
    {
        var text = _input.Text ?? string.Empty;
        if (text.Trim().Length == 0)
        {
            _output.Text = L.Get("Strings.urlCleanerOutputPlaceholder");
            _output.Opacity = 0.55;
            _message.Text = string.Empty;
            _copy.IsEnabled = false;
            return;
        }

        var outcome = _cleaner.Evaluate(text);
        _output.Text = outcome.Url ?? string.Empty;
        _output.Opacity = 1;
        _copy.IsEnabled = outcome.Url is not null;
        _message.Text = _justCopied ? L.Get("Strings.urlCleanerCopied") : UrlCleanerService.Message(outcome);
    }
}

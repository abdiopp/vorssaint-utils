// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Actions;
using Rivet.Core.Contracts;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.ScreenshotEditor;
using Rivet.Core.Shortcuts;
using CoreModifiers = Rivet.Core.Shortcuts.KeyModifiers;
using KeyModifiers = Avalonia.Input.KeyModifiers;
using PixelPoint = Avalonia.PixelPoint;

namespace Rivet.App.Features.ScreenshotEditor;

/// <summary>
/// One editor window per capture (spec 01 §3.10): a dark stage with the
/// action cluster on top, the tool rail on the left, the canvas, and the info
/// chip, contextual style bar and zoom chip at the bottom.
/// </summary>
internal sealed class EditorWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly EditorController _controller;
    private readonly EditorOutput _output;
    private readonly EditorCanvas _canvas;
    private readonly ScrollViewer _scroller;
    private readonly Canvas _overlay = new() { Background = null };
    private readonly ToolRail _rail;
    private readonly StyleBar _styleBar;
    private readonly BottomBarPanel _bottom;
    private readonly Button _undo;
    private readonly Button _redo;
    private readonly Button _qr;
    private readonly Button _recent;
    private readonly Button _fit;
    private readonly Button _actual;
    private readonly TextBlock _zoomLabel = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = EditorChrome.Primary, MinWidth = 44, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sizeLabel = new() { FontSize = 12, Foreground = EditorChrome.Secondary, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _topBand;
    private TextBox? _textEditor;
    private PointerPressedEventArgs? _dragPress;
    private Task<string?>? _dragFile;
    private bool _forceClose;
    private bool _closed;

    public EditorWindow(IServiceProvider services, ScreenshotEditRequest request)
    {
        _services = services;
        _controller = new EditorController(services, request);
        _output = new EditorOutput(_controller, this);
        RequestedThemeVariant = ThemeVariant.Dark;
        Title = L.Get("screenshot.editorTitle");
        Background = EditorChrome.StageBrush;
        ExtendClientAreaToDecorationsHint = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Styles.Add(EditorChrome.ButtonStyles());

        _canvas = new EditorCanvas(_controller);
        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Panel { Children = { _canvas, _overlay } },
        };
        _scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty || e.Property == ScrollViewer.OffsetProperty || e.Property == BoundsProperty)
            {
                _canvas.SetViewport(_scroller.Viewport, _scroller.Offset);
                PositionTextEditor();
            }
        };
        _canvas.PressStarting += (_, _) => CommitText();
        _canvas.ZoomChanged += (_, _) =>
        {
            UpdateZoomChip();
            Dispatcher.UIThread.Post(PositionTextEditor, DispatcherPriority.Render);
        };
        _canvas.ScrollRequested += (_, offset) =>
        {
            _scroller.UpdateLayout();
            _scroller.Offset = offset;
        };

        _rail = new ToolRail(_controller) { Margin = new Thickness(10, 4, 8, 4), VerticalAlignment = VerticalAlignment.Top };
        _rail.ToolPicked += (_, tool) => SelectTool(tool);

        _styleBar = new StyleBar(_controller, () => new BackdropPopover(_controller), () => new WatermarkPopover(_controller));
        _styleBar.CropRequested += (_, _) => ApplyCrop();

        _undo = EditorChrome.IconButton("ArrowUndo", $"{L.Get("Strings.menuUndo")} (Ctrl+Z)", () => Run(_controller.Session.Undo));
        _redo = EditorChrome.IconButton("ArrowRedo", $"{L.Get("Strings.menuRedo")} (Ctrl+Y)", () => Run(_controller.Session.Redo));
        _qr = EditorChrome.IconButton("QrCode", L.Get("Strings.qrResultTitle"), ShowQr);
        _qr.IsVisible = false;
        _recent = EditorChrome.IconButton("History", L.Get("recentCaptures.title"), ShowRecentCaptures);
        _recent.IsVisible = RecentCapturesAction() is not null;
        _fit = EditorChrome.TextButton(L.Get("win.screenshotEditor.zoomFit"), () => _canvas.Fit(), "Ctrl+0");
        _actual = EditorChrome.TextButton("1:1", () => _canvas.ActualSize(), "Ctrl+1");

        _topBand = BuildTopBand();
        var infoChip = BuildInfoChip();
        var zoomChip = BuildZoomChip();
        _bottom = new BottomBarPanel(infoChip, _styleBar, zoomChip, () => _canvas.IsDragging) { Margin = new Thickness(12, 6, 12, 10) };

        var middle = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        middle.Children.Add(_rail);
        Grid.SetColumn(_scroller, 1);
        middle.Children.Add(_scroller);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(new Border { Background = EditorChrome.StageHighlight, IsHitTestVisible = false, [Grid.RowSpanProperty] = 3 });
        root.Children.Add(_topBand);
        Grid.SetRow(middle, 1);
        root.Children.Add(middle);
        Grid.SetRow(_bottom, 2);
        root.Children.Add(_bottom);
        Content = root;

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _controller.Session.TextEditRequested += (_, id) => BeginTextEditor(id);
        _controller.Session.Changed += (_, _) => OnSessionChanged();
        _controller.Session.ImageChanged += (_, _) =>
        {
            _canvas.ContentChanged();
            UpdateInfoChip();
            UpdateZoomChip();
        };
        _controller.Changed += (_, _) => _canvas.InvalidateVisual();
        _controller.BackdropChanged += (_, _) =>
        {
            // The margin changes the content size, and with it the fit zoom.
            _canvas.ContentChanged();
            UpdateZoomChip();
        };
        _controller.QrChanged += (_, _) => _qr.IsVisible = _controller.Qr is not null;
        Opened += (_, _) =>
        {
            _canvas.SetViewport(_scroller.Viewport, _scroller.Offset);
            Dispatcher.UIThread.Post(_controller.StartBackgroundScans, DispatcherPriority.Background);
        };
        UpdateInfoChip();
        UpdateZoomChip();
        OnSessionChanged();
    }

    internal EditorController Controller => _controller;

    internal EditorCanvas Canvas => _canvas;

    /// <summary>Sizes the window for its capture on the display under the pointer (spec 01 §6.5) and centres it.</summary>
    public void PlaceOnPointerDisplay()
    {
        var screens = _services.GetService<IScreenService>();
        var screen = screens?.ScreenFromPoint(screens.CursorPosition);
        var scale = screen?.Scale ?? 1;
        var work = screen?.WorkArea ?? new Rivet.Core.Platform.PixelRect(0, 0, 1920, 1040);
        var vw = work.Width / scale;
        var vh = work.Height / scale;
        var image = _controller.BaseImage;
        var (w, h) = EditorLayoutMath.InitialSize(vw, vh, image.Width / _controller.Scale, image.Height / _controller.Scale);
        var (minW, minH, _, _) = EditorLayoutMath.WindowLimits(vw, vh);
        MinWidth = minW;
        MinHeight = minH;
        Width = w;
        Height = h;
        Position = new PixelPoint((int)(work.X + ((work.Width - (w * scale)) / 2)), (int)(work.Y + ((work.Height - (h * scale)) / 2)));
    }

    // ───────────────────────────── Layout pieces ─────────────────────────────

    private Border BuildTopBand()
    {
        var discard = EditorChrome.IconButton("Delete", $"{L.Get("screenshot.discardConfirm")} (Ctrl+Delete)", Discard);
        var pin = EditorChrome.IconButton("Pin", L.Get("win.screenshotEditor.pinTooltip"), () => _ = PinAsync());
        var share = EditorChrome.IconButton("Share", L.Get("screenshot.shareButton"), () => _ = ShareAsync());
        share.IsVisible = _services.GetService<IShareSheet>()?.IsAvailable == true;

        var saveMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        saveMenu.Opening += (_, _) =>
        {
            saveMenu.Items.Clear();
            var save = new MenuItem { Header = L.Get("screenshot.saveButton"), InputGesture = new KeyGesture(Key.S, KeyModifiers.Control) };
            save.Click += (_, _) => _ = SaveAsync();
            var saveAs = new MenuItem { Header = L.Get("screenshot.saveAsButton"), InputGesture = new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift) };
            saveAs.Click += (_, _) => _ = SaveAsAsync();
            saveMenu.Items.Add(save);
            saveMenu.Items.Add(saveAs);
            if (_output.CanAddToShelf)
            {
                var shelf = new MenuItem { Header = L.Get("screenshot.addToShelfButton") };
                shelf.Click += (_, _) => _ = AddToShelfAsync();
                saveMenu.Items.Add(shelf);
            }
        };
        var saveButton = new SplitButton
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { EditorChrome.Icon("Save", 15), new TextBlock { Text = L.Get("screenshot.saveButton"), FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } } },
            Flyout = saveMenu,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 3),
        };
        saveButton.Click += (_, _) => _ = SaveAsync();
        ToolTip.SetTip(saveButton, "Ctrl+S");
        AutomationProperties.SetName(saveButton, L.Get("screenshot.saveButton"));

        var copy = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FluentIcons.Avalonia.SymbolIcon { Symbol = FluentIcons.Common.Symbol.Copy, FontSize = 15 }, new TextBlock { Text = L.Get("screenshot.copyButton"), FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } } },
            Classes = { "accent" },
            Focusable = false,
            Padding = new Thickness(12, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        copy.Click += (_, _) => _ = CopyAsync();
        ToolTip.SetTip(copy, L.Get("win.screenshotEditor.enterKey"));
        AutomationProperties.SetName(copy, L.Get("screenshot.copyButton"));

        var cluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _recent, discard, EditorChrome.VerticalDivider(), _undo, _redo, EditorChrome.VerticalDivider(), _qr, pin, share, EditorChrome.VerticalDivider(), saveButton, copy },
        };
        var capsule = EditorChrome.Capsule(cluster, 12, new Thickness(9, 4));
        capsule.HorizontalAlignment = HorizontalAlignment.Right;
        capsule.Margin = new Thickness(0, 0, CaptionButtonsWidth(right: true), 0);

        var brand = new AppGlyph { Width = 20, Height = 20, Foreground = EditorChrome.BrandMark, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var band = new Border
        {
            Height = 48,
            Padding = new Thickness(12 + CaptionButtonsWidth(right: false), 10, 12, 0),
            Background = Brushes.Transparent,
            Child = new Panel { Children = { brand, capsule } },
        };
        // Only the title strip moves the window; dragging the canvas never does.
        band.PointerPressed += (_, e) =>
        {
            if (e.Source == band || e.Source is Panel)
            {
                if (e.ClickCount >= 2)
                {
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                }
                else if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    BeginMoveDrag(e);
                }
            }
        };
        return band;
    }

    /// <summary>Room for the system caption buttons drawn over the extended client area.</summary>
    private static double CaptionButtonsWidth(bool right) =>
        OperatingSystem.IsWindows() ? (right ? 140 : 0) : OperatingSystem.IsMacOS() ? (right ? 0 : 70) : 0;

    private Control BuildInfoChip()
    {
        var handle = new Border
        {
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.DragCopy),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children = { EditorChrome.Icon("DocumentArrowUp", 15), new TextBlock { Text = L.Get("screenshot.dragOutHandleLabel"), FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = EditorChrome.Primary, VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        ToolTip.SetTip(handle, L.Get("screenshot.dragOutHandleLabel"));
        AutomationProperties.SetName(handle, L.Get("screenshot.dragOutHandleLabel"));
        handle.PointerPressed += OnDragHandlePressed;
        handle.PointerMoved += OnDragHandleMoved;
        handle.PointerReleased += (_, _) => _dragPress = null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { handle, _sizeLabel } };
        var chip = EditorChrome.Capsule(row, 14, new Thickness(12, 6));
        chip.HorizontalAlignment = HorizontalAlignment.Left;
        chip.VerticalAlignment = VerticalAlignment.Center;
        return chip;
    }

    private Control BuildZoomChip()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _fit, _zoomLabel, _actual } };
        var chip = EditorChrome.Capsule(row, 9, new Thickness(4, 3));
        ToolTip.SetTip(chip, L.Get("win.screenshotEditor.zoomTooltip"));
        chip.HorizontalAlignment = HorizontalAlignment.Right;
        chip.VerticalAlignment = VerticalAlignment.Center;
        return chip;
    }

    private void UpdateInfoChip()
    {
        var image = _controller.BaseImage;
        var text = L.Format("win.screenshotEditor.pixelSizeFormat", image.Width, image.Height);
        if (EditorFiles.ScaleLabel(_controller.Scale) is { } scale)
        {
            text += "  " + scale;
        }

        _sizeLabel.Text = text;
    }

    private void UpdateZoomChip()
    {
        _zoomLabel.Text = string.Format(CultureInfo.CurrentCulture, "{0} %", _canvas.ZoomPercent);
        EditorChrome.SetActive(_fit, _canvas.FitMode);
        EditorChrome.SetActive(_actual, _canvas.IsActualSize);
    }

    private void OnSessionChanged()
    {
        var session = _controller.Session;
        _undo.IsEnabled = session.CanUndo;
        _redo.IsEnabled = session.CanRedo;
        _canvas.InvalidateVisual();
        if (_textEditor is not null)
        {
            if (session.EditingTextId is null)
            {
                RemoveTextEditor();
            }
            else
            {
                StyleTextEditor();
            }
        }
    }

    // ───────────────────────────── Commands ─────────────────────────────

    private void Run(Action action)
    {
        CommitText();
        action();
    }

    private void SelectTool(EditorTool tool)
    {
        CommitText();
        _controller.Session.SetTool(tool);
    }

    private void ApplyCrop()
    {
        CommitText();
        _controller.Session.ApplyCrop();
    }

    private async Task<bool> Guarded(Func<Task<bool>> operation)
    {
        CommitText();
        if (_controller.IsBusy)
        {
            return false;
        }

        _controller.IsBusy = true;
        try
        {
            return await operation();
        }
        finally
        {
            _controller.IsBusy = false;
        }
    }

    private async Task CopyAsync()
    {
        if (await Guarded(_output.CopyAsync))
        {
            FinishAndClose();
        }
    }

    private async Task SaveAsync()
    {
        if (await Guarded(_output.SaveAsync))
        {
            FinishAndClose();
        }
    }

    private async Task SaveAsAsync()
    {
        if (await Guarded(_output.SaveAsAsync))
        {
            FinishAndClose();
        }
    }

    private async Task AddToShelfAsync()
    {
        if (await Guarded(_output.AddToShelfAsync))
        {
            FinishAndClose();
        }
    }

    private async Task PinAsync()
    {
        if (await Guarded(_output.PinAsync))
        {
            _controller.MarkClean();
        }
    }

    private async Task ShareAsync() => await Guarded(_output.ShareAsync);

    /// <summary>Every final output (Copy, Save, Save As, Add to Shelf) closes the editor.</summary>
    private void FinishAndClose()
    {
        _controller.MarkClean();
        _forceClose = true;
        Close();
    }

    /// <summary>The trash button and Ctrl+Delete close without asking.</summary>
    private void Discard()
    {
        CommitText();
        _forceClose = true;
        Close();
    }

    private void ShowQr()
    {
        CommitText();
        if (_controller.Qr is { } qr)
        {
            QrResultWindow.ShowFor(_services, qr, this);
        }
    }

    private string? RecentCapturesAction()
    {
        var actions = _services.GetService<ActionRegistry>();
        return actions?.Get(RecentCaptureActions.ShowPalette) is { } action && actions.IsEnabled(action) ? action.Id : null;
    }

    private void ShowRecentCaptures()
    {
        CommitText();
        if (RecentCapturesAction() is { } id)
        {
            _ = _services.GetRequiredService<ActionRegistry>().InvokeAsync(id, ActionSource.Other);
        }
    }

    private void CopyOrCopyText()
    {
        if (_controller.Session.SelectedWordsText is { } text)
        {
            // Copying recognized words keeps the editor open.
            _output.CopyText(text);
            return;
        }

        _ = CopyAsync();
    }

    // ───────────────────────────── Drag out ─────────────────────────────

    private void OnDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _controller.IsBusy)
        {
            return;
        }

        CommitText();
        _dragPress = e;
        _dragFile = _output.WriteTempFileAsync();
        e.Handled = true;
    }

    private async void OnDragHandleMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPress is not { } press || _dragFile is not { } pending || sender is not Visual visual)
        {
            return;
        }

        var start = press.GetPosition(visual);
        var now = e.GetPosition(visual);
        if (Math.Abs(now.X - start.X) < 4 && Math.Abs(now.Y - start.Y) < 4)
        {
            return;
        }

        _dragPress = null;
        var path = await pending;
        if (path is null || await StorageProvider.TryGetFileFromPathAsync(new Uri(path)) is not { } file)
        {
            return;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateFile(file));
        await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
        // Dragging the handle counts as an export.
        _controller.MarkClean();
    }

    // ───────────────────────────── Inline text ─────────────────────────────

    private void BeginTextEditor(Guid id)
    {
        if (_controller.Session.Find(id) is not { } annotation)
        {
            return;
        }

        RemoveTextEditor();
        var box = new TextBox
        {
            Text = annotation.Text,
            PlaceholderText = L.Get("screenshot.textPlaceholder"),
            MinWidth = 130,
            Padding = new Thickness(6, 3),
            FontWeight = FontWeight.SemiBold,
            Background = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            AcceptsReturn = false,
            Classes = { "editorText" },
        };
        AutomationProperties.SetName(box, L.Get("screenshot.textPlaceholder"));
        // Fluent paints its own fill while hovered or focused; keep the 35 % black so the capture shows through.
        var fill = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0));
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
        {
            box.Resources[key] = fill;
        }

        foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
        {
            box.Resources[key] = EditorChrome.AccentTint(0.7);
        }

        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                e.Handled = true;
                CommitText();
            }
        }, RoutingStrategies.Tunnel);
        _textEditor = box;
        _overlay.Children.Add(box);
        StyleTextEditor();
        PositionTextEditor();
        // The text field takes focus as soon as it is in the tree (the macOS 0.05 s delay is not needed here).
        Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Loaded);
    }

    private void StyleTextEditor()
    {
        if (_textEditor is not { } box || _controller.Session.EditingTextId is not { } id || _controller.Session.Find(id) is not { } annotation)
        {
            return;
        }

        var color = EditorChrome.ToAvalonia(annotation.Color);
        box.Foreground = new SolidColorBrush(color);
        box.CaretBrush = new SolidColorBrush(color);
        box.BorderBrush = EditorChrome.AccentTint(0.7);
        box.FontSize = Math.Max(11, AnnotationMetrics.TextFontPixels(annotation.TextSize, _controller.Scale) * _canvas.Zoom);
        PositionTextEditor();
    }

    private void PositionTextEditor()
    {
        if (_textEditor is not { } box || _controller.Session.EditingTextId is not { } id || _controller.Session.Find(id) is not { } annotation)
        {
            return;
        }

        var origin = _canvas.ImageToView(AnnotationMetrics.TextDrawOrigin(annotation.Rect));
        Avalonia.Controls.Canvas.SetLeft(box, origin.X - 7);
        Avalonia.Controls.Canvas.SetTop(box, origin.Y - 4);
    }

    /// <summary>Commits the inline text editor (Return, Esc, any canvas press, any toolbar action, any tool change).</summary>
    private void CommitText()
    {
        if (_textEditor is not { } box)
        {
            return;
        }

        var text = box.Text;
        RemoveTextEditor();
        _controller.Session.CommitTextEdit(text);
        Focus();
    }

    private void RemoveTextEditor()
    {
        if (_textEditor is { } box)
        {
            _textEditor = null;
            _overlay.Children.Remove(box);
        }
    }

    // ───────────────────────────── Keyboard ─────────────────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // While a text field has focus every key belongs to it; the shortcut recorder listens on its own.
        if (FocusManager?.GetFocusedElement() is TextBox or KeyField)
        {
            return;
        }

        var session = _controller.Session;
        var vk = AvaloniaKeyMap.ToVirtualKey(e.Key);
        var mods = AvaloniaKeyMap.ToModifiers(e.KeyModifiers);
        var ctrl = mods.HasFlag(CoreModifiers.Control);
        var shift = mods.HasFlag(CoreModifiers.Shift);
        var alt = mods.HasFlag(CoreModifiers.Alt);
        var handled = true;
        if (ctrl && !alt)
        {
            switch (e.Key)
            {
                case Key.C:
                    CopyOrCopyText();
                    break;
                case Key.S when shift:
                    _ = SaveAsAsync();
                    break;
                case Key.S:
                    _ = SaveAsync();
                    break;
                case Key.Z when shift:
                case Key.Y:
                    session.Redo();
                    break;
                case Key.Z:
                    session.Undo();
                    break;
                case Key.P:
                    _ = PinAsync();
                    break;
                case Key.Back or Key.Delete:
                    Discard();
                    break;
                case Key.D0 or Key.NumPad0:
                    _canvas.Fit();
                    break;
                case Key.D1 or Key.NumPad1:
                    _canvas.ActualSize();
                    break;
                case Key.OemPlus or Key.Add:
                    _canvas.ZoomBy(EditorLayoutMath.ZoomStepIn);
                    break;
                case Key.OemMinus or Key.Subtract:
                    _canvas.ZoomBy(EditorLayoutMath.ZoomStepOut);
                    break;
                case Key.W:
                    Close();
                    break;
                default:
                    handled = false;
                    break;
            }
        }
        else if (!ctrl && !alt && e.Key is Key.Back or Key.Delete)
        {
            session.DeleteSelected();
        }
        else if (!ctrl && !alt && e.Key == Key.Enter)
        {
            if (session.Tool == EditorTool.Crop && session.CropDraft is not null)
            {
                ApplyCrop();
            }
            else
            {
                _ = CopyAsync();
            }
        }
        else if (e.Key == Key.Escape && mods == CoreModifiers.None)
        {
            if (!session.HandleEscape())
            {
                Close();
            }
        }
        else
        {
            handled = false;
        }

        if (!handled && vk != 0 && !VirtualKeys.IsModifier(vk))
        {
            int? digit = e.KeySymbol is { Length: 1 } symbol && symbol[0] is >= '1' and <= '9' ? symbol[0] - '0' : null;
            var tool = ToolKeyMap.Resolve(_controller.ToolOrder, _controller.ToolBindings, _controller.ToolShortcutsEnabled,
                new KeyChord(mods, vk), digit, _controller.KeyboardLayout.DigitTypedBy);
            if (tool is { } t)
            {
                SelectTool(t);
                handled = true;
            }
        }

        e.Handled = handled;
    }

    // ───────────────────────────── Closing ─────────────────────────────

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        CommitText();
        if (!_forceClose && _controller.IsDirty)
        {
            e.Cancel = true;
            _ = ConfirmCloseAsync();
        }

        base.OnClosing(e);
    }

    private async Task ConfirmCloseAsync()
    {
        var discard = await ConfirmDialog.ShowAsync(this, L.Get("screenshot.discardTitle"), L.Get("screenshot.discardMessage"),
            L.Get("screenshot.discardConfirm"), L.Get("screenshot.cancel"), destructive: true);
        if (discard)
        {
            _forceClose = true;
            Close();
        }
    }

    /// <summary>Closes without asking (the feature was uninstalled).</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_closed)
        {
            _closed = true;
            _controller.Dispose();
        }
    }
}

/// <summary>
/// The bottom row: info chip left, style bar centred, zoom chip right; when
/// the window is too narrow the style bar takes its own row above the chips.
/// The arrangement never changes mid-drag.
/// </summary>
internal sealed class BottomBarPanel : Panel
{
    private readonly Control _left;
    private readonly Control _center;
    private readonly Control _right;
    private readonly Func<bool> _isDragging;
    private bool _stacked;

    public BottomBarPanel(Control left, Control center, Control right, Func<bool> isDragging)
    {
        _left = left;
        _center = center;
        _right = right;
        _isDragging = isDragging;
        Children.Add(left);
        Children.Add(center);
        Children.Add(right);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _left.Measure(unbounded);
        _center.Measure(unbounded);
        _right.Measure(unbounded);
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : _left.DesiredSize.Width + _center.DesiredSize.Width + _right.DesiredSize.Width + 24;
        var side = Math.Max(_left.DesiredSize.Width, _right.DesiredSize.Width);
        var fits = (2 * side) + _center.DesiredSize.Width + 24 <= width;
        if (!_isDragging())
        {
            _stacked = !fits;
        }

        var chips = Math.Max(_left.DesiredSize.Height, _right.DesiredSize.Height);
        var height = _stacked ? _center.DesiredSize.Height + 6 + chips : Math.Max(chips, _center.DesiredSize.Height);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var chips = Math.Max(_left.DesiredSize.Height, _right.DesiredSize.Height);
        if (_stacked)
        {
            var cw = Math.Min(finalSize.Width, _center.DesiredSize.Width);
            _center.Arrange(new Rect((finalSize.Width - cw) / 2, 0, cw, _center.DesiredSize.Height));
            var y = _center.DesiredSize.Height + 6;
            _left.Arrange(new Rect(0, y + ((chips - _left.DesiredSize.Height) / 2), _left.DesiredSize.Width, _left.DesiredSize.Height));
            _right.Arrange(new Rect(finalSize.Width - _right.DesiredSize.Width, y + ((chips - _right.DesiredSize.Height) / 2), _right.DesiredSize.Width, _right.DesiredSize.Height));
        }
        else
        {
            var h = finalSize.Height;
            _left.Arrange(new Rect(0, (h - _left.DesiredSize.Height) / 2, _left.DesiredSize.Width, _left.DesiredSize.Height));
            _right.Arrange(new Rect(finalSize.Width - _right.DesiredSize.Width, (h - _right.DesiredSize.Height) / 2, _right.DesiredSize.Width, _right.DesiredSize.Height));
            _center.Arrange(new Rect((finalSize.Width - _center.DesiredSize.Width) / 2, (h - _center.DesiredSize.Height) / 2, _center.DesiredSize.Width, _center.DesiredSize.Height));
        }

        return finalSize;
    }
}

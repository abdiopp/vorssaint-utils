// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;

namespace Rivet.App.Shell;

public sealed partial class PanelTabItem(PanelViewModel owner, PanelSectionDescriptor descriptor) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public PanelSectionDescriptor Descriptor { get; } = descriptor;

    public string Id => Descriptor.Id;

    public string Title => L.Get(Descriptor.TitleKey);

    public Symbol Icon => IconConverter.Parse(Descriptor.Icon);

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            owner.Select(this);
        }
    }
}

/// <summary>State of the tray panel: visible tabs, the current section and the footer actions.</summary>
public sealed partial class PanelViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly PanelRegistry _registry;
    private readonly FeatureRuntime _runtime;
    private readonly ISettingsStore _settings;
    private string? _selectedId;

    [ObservableProperty]
    private Control? _content;

    [ObservableProperty]
    private double _contentMaxHeight = 560;

    public PanelViewModel(IServiceProvider services)
    {
        _services = services;
        _registry = services.GetRequiredService<PanelRegistry>();
        _runtime = services.GetRequiredService<FeatureRuntime>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _registry.Changed += (_, _) => Rebuild();
        _runtime.Changed += (_, _) => Rebuild();
        _settings.Observe(() => Rebuild(), ShellSettings.PanelSectionOrder, ShellSettings.PanelHiddenItems);
        Localizer.Current.LanguageChanged += (_, _) => Rebuild();
    }

    public ObservableCollection<PanelTabItem> Tabs { get; } = [];

    public string ProductName => AppIdentity.DisplayName;

    public bool IsBeta => AppIdentity.IsPrerelease;

    public string SettingsLabel => L.Get("Strings.panelSettings");

    public string QuitLabel => L.Get("Strings.panelQuit");

    /// <summary>The section shown now (for the footer's Settings target and keep-open rules).</summary>
    public PanelSectionDescriptor? CurrentSection => Tabs.FirstOrDefault(t => t.IsSelected)?.Descriptor;

    /// <summary>Raised when the shown section changes (the window re-measures and re-anchors).</summary>
    public event EventHandler? SectionChanged;

    public event EventHandler? OpenSettingsRequested;

    public event EventHandler? QuitRequested;

    /// <summary>Visible sections in panel order.</summary>
    public IReadOnlyList<PanelSectionDescriptor> VisibleSections()
    {
        var hidden = PanelLayout.ParseHidden(_settings.Get(ShellSettings.PanelHiddenItems));
        var candidates = _registry.Sections
            .Where(s => s.FeatureIds.Count == 0 || s.FeatureIds.Any(_runtime.IsAvailable))
            .Where(s => s.IsVisible?.Invoke() ?? true)
            .ToList();
        var ordered = PanelLayout.Order(candidates, s => s.Id, s => s.Order, _settings.Get(ShellSettings.PanelSectionOrder));
        var visible = ordered.Where(s => !hidden.Contains(s.Id)).ToList();
        // Never show an empty panel: if everything is hidden, fall back to the first section.
        return visible.Count > 0 ? visible : ordered.Take(1).ToList();
    }

    public void Rebuild()
    {
        var sections = VisibleSections();
        Tabs.Clear();
        foreach (var section in sections)
        {
            Tabs.Add(new PanelTabItem(this, section));
        }

        OnPropertyChanged(nameof(SettingsLabel));
        OnPropertyChanged(nameof(QuitLabel));
        var target = Tabs.FirstOrDefault(t => t.Id == _selectedId) ?? Tabs.FirstOrDefault();
        if (target is not null)
        {
            _selectedId = null;
            target.IsSelected = true;
            if (_selectedId is null)
            {
                Select(target);
            }
        }
        else
        {
            Content = null;
        }
    }

    public void Focus(string sectionId)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == sectionId);
        if (tab is not null)
        {
            tab.IsSelected = true;
        }
    }

    internal void Select(PanelTabItem tab)
    {
        foreach (var other in Tabs.Where(t => !ReferenceEquals(t, tab) && t.IsSelected))
        {
            other.IsSelected = false;
        }

        if (_selectedId == tab.Id && Content is not null)
        {
            return;
        }

        _selectedId = tab.Id;
        try
        {
            Content = tab.Descriptor.CreateView(_services);
        }
        catch (Exception ex)
        {
            Log.Error("panel", $"Section '{tab.Id}' failed to build.", ex);
            Content = new TextBlock { Text = ex.Message, Classes = { "caption" } };
        }

        SectionChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Quit() => QuitRequested?.Invoke(this, EventArgs.Empty);
}

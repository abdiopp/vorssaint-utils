// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Features;
using Rivet.Core.Localization;

namespace Rivet.App.Features.Sound;

/// <summary>
/// The panel's "Volume mixer" tab (spec 05 §3.4.3): the devices block, the
/// app rows and the options. With only Audio priority installed it shows the
/// priority lists instead.
/// </summary>
internal sealed class MixerSectionView : UserControl
{
    public MixerSectionView(IServiceProvider services)
    {
        var runtime = services.GetRequiredService<FeatureRuntime>();
        var stack = new StackPanel { Spacing = 8 };
        if (runtime.IsAvailable(FeatureIds.Mixer))
        {
            stack.Children.Add(SoundUi.SectionTitle("Strings.mixerSection"));
            stack.Children.Add(SoundUi.Card(new DevicesBlock(services)));
            stack.Children.Add(SoundUi.Card(new MixerAppsView(services), new Thickness(4, 4)));
            stack.Children.Add(new Disclosure(L.Get("Strings.keepAwakeOptions"), new MixerOptionsView(services, inPanel: true), card: true));
        }
        else
        {
            stack.Children.Add(SoundUi.SectionTitle("Strings.audioPrioritySection"));
            stack.Children.Add(SoundUi.Card(new PriorityListsView(services, compact: true)));
        }

        Content = stack;
    }
}

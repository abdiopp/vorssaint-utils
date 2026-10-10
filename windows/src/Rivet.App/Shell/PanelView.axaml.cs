// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Rivet.App.Shell;

public partial class PanelView : UserControl
{
    public PanelView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

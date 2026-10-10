// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Modules;
using Rivet.Core.Shortcuts;
using Rivet.Core.Snippets;

namespace Rivet.Platform.Fake.Snippets;

/// <summary>A US layout, enough for the development build and tests.</summary>
public sealed class FakeKeyTranslator : IKeyTranslator
{
    public string Translate(int virtualKey, int scanCode, KeyModifiers modifiers)
    {
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        return virtualKey switch
        {
            >= 'A' and <= 'Z' => ((char)(shift ? virtualKey : virtualKey + 32)).ToString(),
            >= '0' and <= '9' => shift ? ")!@#$%^&*("[virtualKey - '0'].ToString() : ((char)virtualKey).ToString(),
            VirtualKeys.Space => " ",
            VirtualKeys.Tab => "\t",
            VirtualKeys.Return => "\r",
            VirtualKeys.OemSemicolon => shift ? ":" : ";",
            VirtualKeys.OemPlus => shift ? "+" : "=",
            VirtualKeys.OemComma => shift ? "<" : ",",
            VirtualKeys.OemMinus => shift ? "_" : "-",
            VirtualKeys.OemPeriod => shift ? ">" : ".",
            VirtualKeys.OemQuestion => shift ? "?" : "/",
            VirtualKeys.OemTilde => shift ? "~" : "`",
            VirtualKeys.OemOpenBrackets => shift ? "{" : "[",
            VirtualKeys.OemPipe => shift ? "|" : "\\",
            VirtualKeys.OemCloseBrackets => shift ? "}" : "]",
            VirtualKeys.OemQuotes => shift ? "\"" : "'",
            _ => string.Empty,
        };
    }
}

/// <summary>The classic Windows sound names; playing only logs.</summary>
public sealed class FakeSnippetSounds : ISnippetSounds
{
    public List<string> Played { get; } = [];

    public IReadOnlyList<string> Available() =>
        ["Windows Background", "Windows Ding", "Windows Exclamation", "Windows Notify Calendar", "Windows Notify Email", "Windows Pop-up Blocked", "chimes", "chord", "ding", "notify", "tada"];

    public void Play(string name) => Played.Add(name);
}

public sealed class FakeSnippetsRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IKeyTranslator, FakeKeyTranslator>();
        services.AddSingleton<FakeSnippetSounds>();
        services.AddSingleton<ISnippetSounds>(sp => sp.GetRequiredService<FakeSnippetSounds>());
    }
}

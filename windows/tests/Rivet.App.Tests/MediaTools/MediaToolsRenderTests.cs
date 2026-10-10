// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string) is fine for snapshots.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.MediaTools;
using Rivet.App.Settings;
using Rivet.Core.Features;
using Rivet.Core.Modules.MediaTools;
using Rivet.Core.Settings;
using SkiaSharp;
using Xunit;

namespace Rivet.App.Tests.MediaTools;

public class MediaToolsRenderTests
{
    /// <summary>Every Media setting: tests share one host, and presets (e.g. Social → PNG) would leak into the next test.</summary>
    private static readonly string[] MediaKeys = typeof(MediaSettings)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Select(f => f.GetValue(null)).OfType<SettingDefinition>().Select(d => d.Key).ToArray();

    private static MediaToolsService Service(MediaTool tool)
    {
        var host = TestApp.Host;
        foreach (var key in MediaKeys)
        {
            host.Settings.Reset(key);
        }

        host.Services.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.MediaTools, true);
        var service = host.Services.GetRequiredService<MediaToolsService>();
        service.ClearInputs();
        service.SetTool(tool);
        return service;
    }

    private static string SampleImage(string name, int width = 640, int height = 400)
    {
        var folder = Path.Combine(Path.GetTempPath(), "rivet-media-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [SKColors.Orange, SKColors.MediumPurple], SKShaderTileMode.Clamp) };
        surface.Canvas.DrawRect(0, 0, width, height, paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static ThemeVariant Theme(string theme) => theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_panel_workspace_renders_every_tool(string theme)
    {
        foreach (var tool in new[] { MediaTool.Video, MediaTool.Gif, MediaTool.Text })
        {
            var service = Service(tool);
            var view = new MediaWorkspaceView(TestApp.Host.Services, MediaHost.Panel);
            TestApp.Snapshot(new Border { Padding = new Thickness(12), Child = view }, $"media-panel-{tool.ToString().ToLowerInvariant()}-{theme}", 340, 640, Theme(theme));
            Assert.Equal(tool, service.Tool);
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_image_tool_shows_options_preview_and_watermark(string theme)
    {
        var service = Service(MediaTool.Image);
        var settings = TestApp.Host.Services.GetRequiredService<ISettingsStore>();
        settings.Set(MediaSettings.WatermarkKind, "text");
        settings.Set(MediaSettings.WatermarkText, "Rivet");
        service.SetInputs([SampleImage("photo.png")]);
        Assert.Single(service.Inputs);
        Assert.NotNull(service.ImageSize);
        Assert.EndsWith(".jpg", service.Output);
        var view = new MediaWorkspaceView(TestApp.Host.Services, MediaHost.Window);
        var window = new Window { Width = 560, Height = 1100, Content = new Border { Padding = new Thickness(18), Child = view }, RequestedThemeVariant = Theme(theme) };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("WindowBackgroundBrush").ToBinding());
        window.Show();

        // The preview renders off the UI thread after a short debounce.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (window.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null))
            {
                break;
            }

            Thread.Sleep(20);
        }

        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(TestApp.SnapshotDirectory, $"media-window-image-{theme}.png"));
        window.Close();
        settings.Set(MediaSettings.WatermarkKind, "off");
    }

    [AvaloniaFact]
    public async Task Processing_an_image_writes_the_output_next_to_the_source()
    {
        var service = Service(MediaTool.Image);
        var settings = TestApp.Host.Services.GetRequiredService<ISettingsStore>();
        service.ApplyImageOptions(MediaImagePresets.Web);
        var input = SampleImage("holiday.png", 3200, 2000);
        service.SetInputs([input]);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(input)!, "holiday-web.jpg"), service.Output);
        await service.RunAsync();
        Assert.Equal(MediaJobPhase.Completed, service.Job.Phase);
        var output = Assert.Single(service.Job.Result!.Outputs);
        Assert.True(File.Exists(output));
        using var codec = SKCodec.Create(output);
        Assert.Equal(1600, codec.Info.Width);
        Assert.Equal(1000, codec.Info.Height);
        settings.Set(MediaSettings.ImageRenamePattern, string.Empty);
    }

    [AvaloniaFact]
    public async Task A_batch_goes_into_the_converted_subfolder_and_reports_each_file()
    {
        var service = Service(MediaTool.Image);
        var settings = TestApp.Host.Services.GetRequiredService<ISettingsStore>();
        service.ApplyImageOptions(MediaImagePresets.Social);
        settings.Set(MediaSettings.ImageSaveInSubfolder, true);
        var first = SampleImage("a.png", 300, 200);
        var second = Path.Combine(Path.GetDirectoryName(first)!, "b.png");
        File.Copy(first, second);
        service.SetInputs([first, second, Path.Combine(Path.GetDirectoryName(first)!, "notes.txt")]);
        Assert.Equal(2, service.Inputs.Count);
        Assert.True(service.IsBatch);
        await service.RunAsync();
        var result = service.Job.Result!;
        Assert.Equal(2, result.Succeeded);
        Assert.All(result.Outputs, o => Assert.Equal("Converted", Path.GetFileName(Path.GetDirectoryName(o))));
        Assert.Contains("a.png -> a-social.png", service.Summary(result), StringComparison.Ordinal);
        settings.Set(MediaSettings.ImageSaveInSubfolder, false);
        settings.Set(MediaSettings.ImageRenamePattern, string.Empty);
    }

    [AvaloniaFact]
    public async Task Running_without_input_fails_with_the_spec_message()
    {
        var service = Service(MediaTool.Video);
        await service.RunAsync();
        Assert.Equal(MediaJobPhase.Failed, service.Job.Phase);
        Assert.Equal("Choose a file first.", service.Job.Error);
    }

    [AvaloniaFact]
    public void The_settings_page_renders()
    {
        var host = TestApp.Host;
        Service(MediaTool.Image);
        foreach (var theme in new[] { "light", "dark" })
        {
            var vm = new SettingsViewModel(host.Services);
            vm.Navigate(MediaToolsModule.PageId);
            Assert.Equal(MediaToolsModule.PageId, vm.CurrentPageId);
            var window = new SettingsWindow(vm) { Width = 1080, Height = 1100, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
            window.Show();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(TestApp.SnapshotDirectory, $"settings-mediaTools-{theme}.png"));
            window.Close();
        }
    }
}

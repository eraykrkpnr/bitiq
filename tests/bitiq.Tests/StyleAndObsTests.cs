using Bitiq.Core.Models;
using Bitiq.Core.Obs;
using Bitiq.Core.Styling;
using Xunit;

namespace Bitiq.Tests;

public sealed class StyleAndObsTests
{
    [Fact]
    public void StyleCssMapper_MapsAllFieldsAndScalesToVh()
    {
        var profile = new StyleProfile
        {
            FontFamily = "Consolas",
            FontSize = 54,
            FontWeight = 800,
            IsItalic = true,
            Uppercase = true,
            TextColorHex = "#FF00AA",
            TextOpacity = 50,
            BackgroundMode = SubtitleBackgroundMode.PerLineBox,
            BackgroundColorHex = "#102030",
            BackgroundOpacity = 80,
            ShowOriginalText = true,
            OriginalFontSize = 27
        };

        var css = StyleCssMapper.Map(profile);

        Assert.Contains("Consolas", css.FontFamilyCss);
        Assert.Equal("5vh", css.FontSizeVh); // 54 / 1080 * 100 = 5vh
        Assert.Equal("2.5vh", css.OriginalFontSizeVh);
        Assert.Equal(800, css.FontWeight);
        Assert.Equal("italic", css.FontStyle);
        Assert.Equal("uppercase", css.TextTransform);
        Assert.Equal("rgba(255, 0, 170, 0.5)", css.TextColorRgba);
        Assert.Equal("rgba(16, 32, 48, 0.8)", css.BackgroundRgba);
        Assert.Equal("PerLineBox", css.BackgroundMode);
        Assert.True(css.ShowOriginalText);
    }

    [Fact]
    public void StyleProfile_JsonRoundTripPreservesProperties()
    {
        var original = StyleProfile.CreateBuiltInPresets()[2];
        original.Name = "Benim Yayın Stilim";
        original.ShowOriginalText = true;
        original.LingerAfterDisappearMs = 750;

        string json = original.ToJson();
        var loaded = StyleProfile.FromJson(json);

        Assert.NotNull(loaded);
        Assert.Equal("Benim Yayın Stilim", loaded!.Name);
        Assert.False(loaded.IsBuiltIn);
        Assert.True(loaded.ShowOriginalText);
        Assert.Equal(750, loaded.LingerAfterDisappearMs);
        Assert.Equal(original.BackgroundMode, loaded.BackgroundMode);
    }

    [Fact]
    public async Task ObsHttpServer_StartsWithoutAdminAndServesHtmlAndState()
    {
        using var server = new ObsHttpServer(new StyleProfile());
        server.Start(preferredPort: 0);

        Assert.True(server.IsRunning);
        Assert.StartsWith("http://127.0.0.1:", server.Url);

        using var client = new System.Net.Http.HttpClient();
        string html = await client.GetStringAsync(server.Url);
        Assert.Contains("bitiq OBS Overlay", html);
        Assert.Contains("background: transparent", html);
        Assert.Contains("EventSource('/events')", html);

        server.PublishSubtitle(new SubtitleUpdateEvent
        {
            SequenceId = 1,
            EnglishText = "Hello",
            TurkishText = "Merhaba",
            IsCleared = false
        });

        string stateJson = await client.GetStringAsync(server.Url + "state");
        Assert.Contains("Merhaba", stateJson);
    }
}

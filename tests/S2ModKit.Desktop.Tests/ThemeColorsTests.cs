using Avalonia.Media;
using S2ModKit.Desktop;

namespace S2ModKit.Desktop.Tests;

public sealed class ThemeColorsTests
{
    [Fact]
    public void EveryReadyMadeThemeGivesEveryColourButMayLeaveTheAccentToTheSystem()
    {
        foreach (var (name, _) in ThemeColors.Themes)
        {
            var colors = ThemeColors.Resolve(name, null);

            Assert.Equal(ThemeColors.Roles.Select(role => role.Key).Order(), colors.Keys.Order());
            Assert.All(colors.Where(pair => pair.Key != ThemeColors.Accent), pair => Assert.NotNull(pair.Value));
        }

        Assert.Null(ThemeColors.Resolve(ThemeColors.DefaultName, null)[ThemeColors.Accent]);
        Assert.NotNull(ThemeColors.Resolve("Midnight", null)[ThemeColors.Accent]);
    }

    [Fact]
    public void AnUnknownThemeIsTheDefaultOne()
    {
        Assert.Equal(ThemeColors.Resolve(ThemeColors.DefaultName, null), ThemeColors.Resolve("Gone", null));
        Assert.Equal(ThemeColors.Resolve(ThemeColors.DefaultName, null), ThemeColors.Resolve(null, null));
    }

    [Fact]
    public void ChangedColoursGoOnTopAndUnreadableOnesAreLeftOut()
    {
        var colors = ThemeColors.Resolve("Forest", new Dictionary<string, string>
        {
            ["side"] = "#102030",
            ["main"] = "not a colour",
            ["nothing"] = "#FFFFFF",
            [ThemeColors.Accent] = ThemeColors.SystemValue,
        });

        Assert.Equal(Color.FromRgb(0x10, 0x20, 0x30), colors["side"]);
        Assert.Equal(ThemeColors.Resolve("Forest", null)["main"], colors["main"]);
        Assert.False(colors.ContainsKey("nothing"));
        Assert.Null(colors[ThemeColors.Accent]);                          // the system's accent instead of the theme's
    }

    [Theory]
    [InlineData("#5865F2", true)]
    [InlineData("5865f2", true)]
    [InlineData("#5865F", false)]
    [InlineData("#GG0000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HexColoursAreRead(string? text, bool readable)
    {
        Assert.Equal(readable, ThemeColors.TryParse(text, out var color));
        if (readable)
        {
            Assert.Equal("#5865F2", ThemeColors.Hex(color));
        }
    }
}

using S2ModKit.Desktop;

namespace S2ModKit.Desktop.Tests;

public sealed class OneClickLinkTests
{
    [Theory]
    [InlineData("s2modkit:https://gamebanana.com/mmdl/1234567,Mod,654321")]
    [InlineData("S2ModKit://https://gamebanana.com/mmdl/1234567,Mod,654321")]
    [InlineData(" s2modkit:https://files.gamebanana.com/mmdl/1234567,mod,654321 ")]
    public void AGameBananaLinkNamesItsFileAndMod(string text)
    {
        Assert.Equal(new OneClickLink(1234567, 654321), OneClickLink.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--by-hero")]
    [InlineData("other:https://gamebanana.com/mmdl/1,Mod,2")]
    [InlineData("s2modkit:http://gamebanana.com/mmdl/1,Mod,2")]
    [InlineData("s2modkit:https://gamebanana.com.example.org/mmdl/1,Mod,2")]
    [InlineData("s2modkit:https://example.org/mmdl/1,Mod,2")]
    [InlineData("s2modkit:https://gamebanana.com/mmdl/1,Sound,2")]
    [InlineData("s2modkit:https://gamebanana.com/dl/1,Mod,2")]
    [InlineData("s2modkit:https://gamebanana.com/mmdl/x,Mod,2")]
    [InlineData("s2modkit:https://gamebanana.com/mmdl/1,Mod,-2")]
    [InlineData("s2modkit:https://gamebanana.com/mmdl/1,Mod")]
    public void AnythingElseIsNoLink(string? text)
    {
        Assert.Null(OneClickLink.Parse(text));
    }

    [Fact]
    public void ALinkStartsTheAppWithItAsItsOption()
    {
        var options = AppOptions.Parse(["s2modkit:https://gamebanana.com/mmdl/11,Mod,22"]);

        Assert.Equal(new OneClickLink(11, 22), options.Link);
    }
}

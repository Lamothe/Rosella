using Rosella.Compositor;
using Xunit;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The Bing daily-image archive: parsing and URL building are pure, so
/// everything here is offline. The market is the point — the desktop asks
/// for Australia's picture of the day.
/// </summary>
public sealed class BingWallpaperTests
{
    [Fact]
    public void Market_IsAustralia()
    {
        Assert.Equal("en-AU", BingArchive.Market);
        Assert.Contains("mkt=en-AU", BingArchive.RequestUrl());
    }

    [Fact]
    public void Parse_ReadsTheImageFields()
    {
        const string Json = """
            {"images":[{"startdate":"20261030","urlbase":"/th?id=OHR.Something_EN-AU123","title":"A Sea Stack","copyright":"(c) Someone"}],"tooltips":{}}
            """;

        var image = BingArchive.Parse(Json);
        Assert.NotNull(image);
        Assert.Equal("/th?id=OHR.Something_EN-AU123", image.UrlBase);
        Assert.Equal("A Sea Stack", image.Title);
        Assert.Equal("(c) Someone", image.Copyright);
        Assert.Equal("20261030", image.StartDate);
    }

    [Fact]
    public void Parse_ToleratesMissingOptionalFields()
    {
        var image = BingArchive.Parse("""{"images":[{"urlbase":"/th?id=X"}]}""");
        Assert.NotNull(image);
        Assert.Equal(string.Empty, image.Title);
        Assert.Equal(string.Empty, image.Copyright);
    }

    [Theory]
    [InlineData("""{"images":[]}""")]
    [InlineData("""{"images":[{"title":"no urlbase"}]}""")]
    [InlineData("""{"tooltips":{}}""")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData("""{"images":"nope"}""")]
    [InlineData("""{"images":[42]}""")]
    [InlineData("""{"images":[{"urlbase":42}]}""")]
    [InlineData("""{"images":[{"urlbase":null}]}""")]
    [InlineData("""{"images":[{"urlbase":"/th?id=X""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void Parse_ReturnsNullWhenThereIsNoUsableImage(string json)
    {
        Assert.Null(BingArchive.Parse(json));
    }

    [Fact]
    public void Parse_ToleratesWronglyTypedOptionalFields()
    {
        // The picture is usable even when title/copyright/startdate carry
        // the wrong JSON type: they come back empty, not as a crash.
        var image = BingArchive.Parse("""{"images":[{"urlbase":"/th?id=X","title":5,"copyright":null,"startdate":false}]}""");
        Assert.NotNull(image);
        Assert.Equal(string.Empty, image.Title);
        Assert.Equal(string.Empty, image.Copyright);
        Assert.Equal(string.Empty, image.StartDate);
    }

    [Fact]
    public void ImageUrl_BuildsTheFullPictureAddress()
    {
        Assert.Equal(
            "https://www.bing.com/th?id=OHR.X_UHD.jpg",
            BingArchive.ImageUrl("/th?id=OHR.X"));
        Assert.Equal(
            "https://www.bing.com/th?id=OHR.X_FHD.jpg",
            BingArchive.ImageUrl("/th?id=OHR.X", ultraHighDefinition: false));
    }
}

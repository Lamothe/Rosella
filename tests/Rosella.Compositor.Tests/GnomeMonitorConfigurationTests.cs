using System.Globalization;
using Rosella.Compositor;

namespace Rosella.Compositor.Tests;

public sealed class GnomeMonitorConfigurationTests
{
    private static Dictionary<string, int> Connected(params string[] names) =>
        names.Select((name, index) => (name, index)).ToDictionary(pair => pair.name, pair => pair.index);

    private static string Spec(string connector) => $"<monitorspec><connector>{connector}</connector><vendor>V</vendor><product>P</product><serial>S</serial></monitorspec>";
    private static string Monitor(string connector, string mode = "<width>1920</width><height>1080</height><rate>59.9401</rate>") =>
        $"<monitor>{Spec(connector)}<mode>{mode}</mode></monitor>";
    private static string Logical(string monitors, string primary = "yes") =>
        $"<logicalmonitor><x>0</x><y>0</y><scale>1</scale><primary>{primary}</primary>{monitors}</logicalmonitor>";
    private static string Config(string content) => $"<configuration>{content}</configuration>";
    private static string Store(string content) => $"<monitors version=\"2\">{content}</monitors>";

    [Fact]
    public void SelectsExactConnectedSetFromHistoricalConfigurationsIncludingDisabled()
    {
        var xml = Store(Config(Logical(Monitor("eDP-1"))) +
                        Config(Logical(Monitor("DP-1")) + Logical(Monitor("HDMI-1"), "no") +
                               $"<disabled>{Spec("eDP-1")}</disabled>"));
        var result = GnomeMonitorConfiguration.Select(xml, Connected("HDMI-1", "eDP-1", "DP-1"));
        Assert.Equal(new GnomePrimaryMonitor("DP-1", 1920, 1080, 59.9401), result);
        Assert.Null(GnomeMonitorConfiguration.Select(xml, Connected("DP-1", "HDMI-1")));
    }

    [Fact]
    public void MatchingIsOrdinalEvenWhenDictionaryIsCaseInsensitive()
    {
        var connected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["dp-1"] = 1 };
        Assert.Null(GnomeMonitorConfiguration.Select(Store(Config(Logical(Monitor("DP-1")))), connected));
    }

    [Fact]
    public void RejectsMatchingConfigurationsEvenIfPrimaryIsTheSame()
    {
        var config = Config(Logical(Monitor("DP-1")));
        Assert.Null(GnomeMonitorConfiguration.Select(Store(config + config), Connected("DP-1")));
    }

    [Fact]
    public void RejectsRepeatedConnectorsIncludingDisabledAndLeased()
    {
        foreach (var suffix in new[] { Logical(Monitor("DP-1"), "no"), $"<disabled>{Spec("DP-1")}</disabled>", $"<forlease>{Spec("DP-1")}</forlease>" })
        {
            Assert.Null(GnomeMonitorConfiguration.Select(Store(Config(Logical(Monitor("DP-1")) + suffix)), Connected("DP-1")));
        }
    }

    [Fact]
    public void SupportsLeasedConnectorsAsConnected()
    {
        var xml = Store(Config(Logical(Monitor("DP-1")) + $"<forlease>{Spec("HDMI-1")}</forlease>"));
        Assert.NotNull(GnomeMonitorConfiguration.Select(xml, Connected("DP-1", "HDMI-1")));
        Assert.Null(GnomeMonitorConfiguration.Select(xml, Connected("DP-1")));
    }

    [Fact]
    public void RequiresExactlyOneUnmirroredPrimary()
    {
        var logical = Logical(Monitor("DP-1"));
        var cases = new (string Content, string[] Names)[]
        {
            (Logical(Monitor("DP-1"), "no"), ["DP-1"]),
            (logical + Logical(Monitor("HDMI-1")), ["DP-1", "HDMI-1"]),
            (Logical(Monitor("DP-1") + Monitor("HDMI-1")), ["DP-1", "HDMI-1"]),
            (Logical(Monitor("DP-1"), "maybe"), ["DP-1"]),
            (logical.Replace("<primary>yes</primary>", "<primary>yes</primary><primary>no</primary>"), ["DP-1"]),
        };
        foreach (var (content, names) in cases)
        {
            Assert.Null(GnomeMonitorConfiguration.Select(Store(Config(content)), Connected(names)));
        }
    }

    [Theory]
    [InlineData("<width>0</width><height>1080</height><rate>60</rate>")]
    [InlineData("<width>-1</width><height>1080</height><rate>60</rate>")]
    [InlineData("<width>2147483648</width><height>1080</height><rate>60</rate>")]
    [InlineData("<width>1920</width><height>0</height><rate>60</rate>")]
    [InlineData("<width>1920</width><height>1080</height><rate>NaN</rate>")]
    [InlineData("<width>1920</width><height>1080</height><rate>Infinity</rate>")]
    [InlineData("<width>1920</width><height>1080</height><rate>0</rate>")]
    [InlineData("<width>1920</width><height>1080</height><rate>1,5</rate>")]
    [InlineData("<width>1920</width><width>1920</width><height>1080</height><rate>60</rate>")]
    [InlineData("<width>1920</width><height>1080</height>")]
    [InlineData("<width><nested>1920</nested></width><height>1080</height><rate>60</rate>")]
    public void RejectsInvalidModes(string mode)
    {
        Assert.Null(GnomeMonitorConfiguration.Select(Store(Config(Logical(Monitor("DP-1", mode)))), Connected("DP-1")));
    }

    [Fact]
    public void RateUsesInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var result = GnomeMonitorConfiguration.Select(Store(Config(Logical(Monitor("DP-1")))), Connected("DP-1"));
            Assert.Equal(59.9401, result!.Rate);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void RejectsMalformedXmlWrongVersionAndDtd()
    {
        var xmls = new[] { "<monitors", "<monitors version='1'/>", "<monitors/>", "<wrong version='2'/>", "<!DOCTYPE monitors [<!ENTITY e 'DP-1'>]><monitors version='2'>" + Config(Logical(Monitor("&e;"))) + "</monitors>" };
        foreach (var xml in xmls)
        {
            Assert.Null(GnomeMonitorConfiguration.Select(xml, Connected("DP-1")));
        }
    }

    [Fact]
    public void IgnoresMalformedUnmatchedHistoricalConfigurations()
    {
        var valid = Config(Logical(Monitor("DP-1")));
        var invalid = Config(Logical(Monitor("DP-2", "")));
        Assert.NotNull(GnomeMonitorConfiguration.Select(Store(valid + invalid), Connected("DP-1")));
    }

    [Fact]
    public void IgnoresUnmatchedMirroredHistory()
    {
        var mirrored = Config(Logical(Monitor("DP-2") + Monitor("HDMI-1")));
        var current = Config(Logical(Monitor("DP-1")) + Logical(Monitor("HDMI-1"), "no"));
        Assert.Equal("DP-1", GnomeMonitorConfiguration.Select(Store(mirrored + current), Connected("DP-1", "HDMI-1"))!.Connector);
    }

    [Fact]
    public void LeasedConnectorMayAlsoBeDisabled()
    {
        var xml = Store(Config(Logical(Monitor("DP-1")) + $"<disabled>{Spec("HDMI-1")}</disabled><forlease>{Spec("HDMI-1")}</forlease>"));
        Assert.Equal("DP-1", GnomeMonitorConfiguration.Select(xml, Connected("DP-1", "HDMI-1"))!.Connector);
    }

    [Fact]
    public void ParsesInterlaceFlag()
    {
        var mode = "<width>1920</width><height>1080</height><rate>30</rate><flag>interlace</flag>";
        Assert.True(GnomeMonitorConfiguration.Select(Store(Config(Logical(Monitor("DP-1", mode)))), Connected("DP-1"))!.Interlaced);
    }

    [Fact]
    public void RejectsMisplacedAndNamespacedSelectionElements()
    {
        var valid = Config(Logical(Monitor("DP-1")));
        var invalid = new[]
        {
            Store(valid.Replace("<connector>DP-1</connector>", "<wrapper><connector>DP-1</connector></wrapper><connector>DP-1</connector>")),
            Store(valid.Replace("<primary>yes</primary>", "<primary>yes</primary><wrapper><primary>yes</primary></wrapper>")),
            Store(valid.Replace("</configuration>", "<wrapper>" + Spec("HDMI-1") + "</wrapper></configuration>")),
            Store(valid.Replace("<mode>", "<mode>unexpected")),
            Store(valid.Replace("<monitor>", "<monitor xmlns=\"urn:other\">")),
        };
        foreach (var xml in invalid)
        {
            Assert.Null(GnomeMonitorConfiguration.Select(xml, Connected("DP-1")));
        }
    }

    [Fact]
    public void FileSelectionHandlesMissingInvalidAndValidFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rosella-monitors-{Guid.NewGuid():N}.xml");
        try
        {
            Assert.Null(GnomeMonitorConfiguration.SelectFile(path, Connected("DP-1")));
            File.WriteAllText(path, "<monitors>");
            Assert.Null(GnomeMonitorConfiguration.SelectFile(path, Connected("DP-1")));
            File.WriteAllText(path, Store(Config(Logical(Monitor("DP-1")))));
            Assert.Equal("DP-1", GnomeMonitorConfiguration.SelectFile(path, Connected("DP-1"))!.Connector);
            Assert.Null(GnomeMonitorConfiguration.SelectFile(Path.GetTempPath(), Connected("DP-1")));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

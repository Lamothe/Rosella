namespace Rosella.Compositor.Tests;

/// <summary>The settings panel's PipeWire default nodes.</summary>
public class VolumeControlTests
{
    [Fact]
    public void DefaultNode_MapsEachSliderToItsPipeWireDefault()
    {
        Assert.Equal("@DEFAULT_AUDIO_SINK@", WpctlVolumeControl.DefaultNode(AudioEndpoint.Output));
        Assert.Equal("@DEFAULT_AUDIO_SOURCE@", WpctlVolumeControl.DefaultNode(AudioEndpoint.Input));
    }
}

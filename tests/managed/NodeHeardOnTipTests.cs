// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The heard-on cell is a mesh name with no room to say which channel centre
/// went with it, so the cell's tooltip does. It used to show the frequency
/// alone, which put " MHz" over nothing on every node that had never been
/// heard on a radio — most of a node list fed by MQTT.
/// </summary>
public class NodeHeardOnTipTests
{
    [Fact]
    public void AMeshAndAFrequencyAreBothNamed() =>
        Assert.Equal("Heard on MediumFast, 913.125 MHz",
                     new NodeRecord { HeardOnPreset = "MediumFast", HeardOnFreqMHz = 913.125 }.HeardOnTip);

    /// <summary>A mesh recorded before the frequency was kept beside it.</summary>
    [Fact]
    public void AMeshWithNoFrequencyIsStillNamed() =>
        Assert.Equal("Heard on MediumFast", new NodeRecord { HeardOnPreset = "MediumFast" }.HeardOnTip);

    [Fact]
    public void AHandTunedPrimaryWithNoNameReadsAsCustom() =>
        Assert.Equal($"Heard on {HeardOn.Custom}, 906.875 MHz",
                     new NodeRecord { HeardOnPreset = HeardOn.Custom, HeardOnFreqMHz = 906.875 }.HeardOnTip);

    /// <summary>An empty cell says why it is empty, which is the whole reason
    /// the tooltip cannot just be the frequency.</summary>
    [Fact]
    public void AnEmptyCellSaysWhyItIsEmpty()
    {
        Assert.Equal("Not heard on a radio since this was recorded", new NodeRecord().HeardOnTip);
        Assert.Contains("MQTT", new NodeRecord { SeenViaMqtt = true }.HeardOnTip);
    }
}

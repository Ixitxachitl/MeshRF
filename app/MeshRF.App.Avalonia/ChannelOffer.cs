// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// One channel a picker offers to send on, as it is named there. Built by
/// <see cref="RadioViewModel.ChannelOffers"/> so every picker in the app
/// offers the same set under the same names.
/// </summary>
public sealed class ChannelOffer
{
    public required ChannelConfig Channel { get; init; }

    /// <summary>What the picker shows: the channel's name, said to be on its
    /// mesh where more than one mesh's channels are on the list.</summary>
    public required string Label { get; init; }

    public string Preset => Channel.Preset;
    public string Name => Channel.Name;

    public override string ToString() => Label;
}

// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using MeshRF.Channels;
using MeshRF.Mesh;

namespace MeshRF;

/// <summary>
/// The mesh a received beacon advertised, resolved against the channels this
/// station already holds so a bubble can offer to add it.
/// </summary>
/// <remarks>
/// An offer is never applied by arriving. Firmware caches one for its client
/// and applies none of it (MeshBeaconListenerModule), and the same rule holds
/// here: adding the channel is a button press. The PSK on the wire is a
/// convenience token that everyone in earshot of the beacon now has, not a
/// secret — which is why an offered channel is stored withholding position.
/// </remarks>
public sealed partial class BeaconOffer : ObservableObject
{
    /// <summary>The list the channel would be added to: empty for the
    /// primary's, otherwise the preset's own.</summary>
    public required string ListName { get; init; }

    /// <summary>What that list is called in front of a reader.</summary>
    public required string ListLabel { get; init; }

    /// <summary>The advertised channel's name, as spelled on the wire. Blank
    /// is a real Meshtastic channel name — it means the channel is called
    /// after the modem preset — and blank is what the sender hashed, so it is
    /// stored verbatim and only filled in for display.</summary>
    public required string ChannelName { get; init; }

    /// <summary>The advertised PSK in the form it arrived in, Meshtastic's
    /// single-byte shorthands included.</summary>
    public required byte[] Psk { get; init; }

    /// <summary>One line naming what was advertised: channel, preset and
    /// region, as far as the beacon said.</summary>
    public required string Summary { get; init; }

    /// <summary>False when the beacon named no channel to add, or when this
    /// station already holds one of that name and key on that mesh.</summary>
    [ObservableProperty] private bool _canAdd;

    /// <summary>Why there is nothing to add, or what adding it did, or what
    /// still stands between the channel and hearing anything on it. Empty when
    /// the offer speaks for itself.</summary>
    [ObservableProperty] private string _statusText = string.Empty;

    public bool HasStatus => StatusText.Length > 0;

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    /// <summary>
    /// Resolves one advertised mesh against the channels already held on it.
    /// </summary>
    /// <param name="listName">The list the channel belongs in, worked out by
    /// the caller: the advertised preset's, or the one it was heard on.</param>
    /// <param name="listLabel">That list's name in front of a reader.</param>
    /// <param name="onThatMesh">Every channel already on it, disabled ones
    /// excluded — those match no packet, so they are not what the offer would
    /// be a duplicate of.</param>
    /// <param name="note">What stands between the channel and hearing
    /// anything on it, when anything does.</param>
    public static BeaconOffer For(MeshBeacon beacon, string listName, string listLabel,
                                  IEnumerable<ChannelConfig> onThatMesh, string note = "")
    {
        // The channel this offer would become: a secondary in that mesh's
        // list, which is what decides what an offered blank key resolves to.
        var held = onThatMesh.Where(c => !c.IsDisabled).ToList();
        var offered = AsChannelOn(beacon, held);

        var offer = new BeaconOffer
        {
            ListName = listName,
            ListLabel = listLabel,
            ChannelName = beacon.ChannelName,
            Psk = beacon.ChannelPsk,
            Summary = SummaryOf(beacon, offered),
            StatusText = note,
        };

        // A preset and a region with no channel leave nothing to add: they say
        // a mesh is out there, and reaching it is the Listeners window's job.
        if (!beacon.HasChannel) return offer;

        if (held.Any(c => IsSameChannel(c, offered)))
        {
            offer.StatusText = $"Already on {listLabel}.";
            return offer;
        }

        // Same name, another key. It is a different channel on the air, but
        // two tabs called "Alta" are indistinguishable once they are drawn, so
        // this is not offered silently — the mesh may have rotated its key, or
        // this may be somebody else's channel of the same name, and only the
        // operator can say which.
        if (held.Any(c => string.Equals(c.Name, beacon.ChannelName, StringComparison.Ordinal)))
        {
            offer.StatusText =
                $"{listLabel} already has a channel called {Quoted(beacon.ChannelName)} with a different key. " +
                "Open its Settings to compare, or rename it, and this offer can be taken then.";
            return offer;
        }

        offer.CanAdd = true;
        return offer;
    }

    /// <summary>
    /// Whether a held channel is the advertised one. Identity on the air is
    /// the name and the key together — the two things the channel hash is
    /// taken over — so both have to match, and the name matches exactly, since
    /// firmware hashes the bytes as spelled.
    /// </summary>
    private static bool IsSameChannel(ChannelConfig held, ChannelConfig offered) =>
        string.Equals(held.Name, offered.Name, StringComparison.Ordinal)
        && held.EffectiveKey.AsSpan().SequenceEqual(offered.EffectiveKey);

    /// <summary>
    /// The advertised channel as it would sit on that mesh, which is the only
    /// form in which its key can be resolved.
    /// </summary>
    /// <remarks>
    /// An advertised PSK may be blank, and blank is not "no key" and not the
    /// default key either: firmware <c>Channels::getKey</c> hands a keyless
    /// secondary its primary's key, and only calls it unencrypted when there
    /// is no primary to borrow from. So the probe is wired to that mesh's
    /// primary exactly as the stored channel will be — without it, an offered
    /// blank key read as unencrypted, which both mislabelled the offer and
    /// stopped it matching the identical channel already held.
    /// </remarks>
    private static ChannelConfig AsChannelOn(MeshBeacon beacon, IReadOnlyList<ChannelConfig> onThatMesh)
    {
        var primary = onThatMesh.FirstOrDefault(c => c.Role == ChannelRole.Primary);
        return new ChannelConfig
        {
            Name = beacon.ChannelName,
            Psk = beacon.ChannelPsk,
            Role = ChannelRole.Secondary,
            PrimaryProvider = () => primary,
        };
    }

    private static string Quoted(string name) => name.Length == 0 ? "with no name" : $"“{name}”";

    /// <summary>One line naming what a beacon advertised.</summary>
    /// <param name="offered">The channel it would become on the mesh it is
    /// offered for, or null when there is no mesh to resolve it against.</param>
    private static string SummaryOf(MeshBeacon beacon, ChannelConfig? offered)
    {
        var parts = new List<string>();
        if (beacon.HasChannel)
        {
            // A blank name is shown as the preset it stands for, since that is
            // what it means, and as nothing at all when there is no preset to
            // name it after.
            string shown = beacon.ChannelName.Length > 0
                ? beacon.ChannelName
                : beacon.Preset?.ToString() ?? string.Empty;
            string key = KeyNote(beacon, offered);
            parts.Add(shown.Length > 0 ? $"channel “{shown}” ({key})" : $"an unnamed channel ({key})");
        }
        if (beacon.Preset is { } preset) parts.Add($"preset {preset}");
        if (beacon.Region != Region.UNSET) parts.Add($"region {beacon.Region}");
        return "Advertises " + string.Join(" · ", parts);
    }

    /// <summary>
    /// What the advertised key amounts to, said as what adding the channel
    /// would actually do rather than as what the bytes literally are.
    /// </summary>
    /// <remarks>
    /// A blank PSK is the case worth spelling out: it is neither "no key" nor
    /// the default key, but an instruction to use whatever the mesh's primary
    /// channel uses — so on a mesh that has one, the offer is as private as
    /// that channel already is, and on one that has none it really is in
    /// clear.
    /// </remarks>
    private static string KeyNote(MeshBeacon beacon, ChannelConfig? offered)
    {
        var key = (offered ?? new ChannelConfig
        {
            Psk = beacon.ChannelPsk,
            Role = ChannelRole.Secondary,
        }).EffectiveKey;

        if (beacon.ChannelPsk.Length == 0)
            return key.Length == 0 ? "no key — in clear" : "no key of its own — uses this mesh's";
        if (key.Length == 0) return "unencrypted";
        return key.AsSpan().SequenceEqual(ChannelConfig.DefaultPsk) ? "default key" : "shared key";
    }

    /// <summary>The channel this offer would create.</summary>
    public ChannelConfig ToChannel(int index) => new()
    {
        Preset = ListName,
        Index = index,
        Name = ChannelName,
        Psk = (byte[])Psk.Clone(),
        Role = ChannelRole.Secondary,
        // Matches the "+" button: a channel arrived at from outside starts out
        // withholding position rather than volunteering one — all the more so
        // when its key came off the air.
        PositionPrecision = 0,
    };
}

// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Nodes;

/// <summary>What makes two node rows one radio.</summary>
public enum NodeIdentityMatch
{
    /// <summary>Different radios, or too little evidence to say otherwise.</summary>
    None,

    /// <summary>Both rows advertised the same MAC.</summary>
    MacAddress,

    /// <summary>One key, and exactly one of the pair already sitting on the
    /// number firmware 2.8 derives from it.</summary>
    PkiUpgrade,
}

/// <summary>
/// Decides when two node rows are one radio that changed its node number.
/// </summary>
/// <remarks>
/// Firmware 2.8 moved the node number off the MAC (bytes 2-5) and onto
/// <c>crc32(public_key)</c>. <c>NodeDB::createNewIdentity</c> drops the old
/// entry from the upgrading node's own database only, so every other node on
/// the mesh keeps a ghost of the number it used to answer to.
///
/// The MAC is what ties the two together, and we are unusually well placed to
/// hold one: <c>TypeConversions::ConvertToUser</c> zero-fills it for anything
/// a node serves out of its NodeDB, which is all a phone client ever sees, but
/// we drive the radio ourselves and take every NodeInfo straight off the air,
/// where it is the sender's own <c>owner</c> record with its real MAC in it.
/// </remarks>
public static class NodeIdentity
{
    /// <summary>Whether two rows describe one radio, and on what evidence.</summary>
    public static NodeIdentityMatch Compare(NodeRecord a, NodeRecord b)
    {
        if (a.NodeNum == b.NodeNum) return NodeIdentityMatch.None;

        // A MAC on both sides settles it either way. Two rows that advertised
        // different MACs are different radios however much else they share --
        // which is what stops a cluster of nodes shipping one duplicated public
        // key (a restored backup, a bad keygen) from collapsing into a node.
        if (a.HasMacAddress && b.HasMacAddress)
            return string.Equals(a.MacAddress, b.MacAddress, StringComparison.OrdinalIgnoreCase)
                ? NodeIdentityMatch.MacAddress
                : NodeIdentityMatch.None;

        // With a MAC missing on either side the key has to carry it alone, and
        // a shared key is exactly the case it cannot tell apart. Take it only
        // in the shape an upgrade actually has: one name, and the number
        // derived from the key claimed by one of the pair but not both.
        if (string.IsNullOrEmpty(a.PublicKey)
            || !string.Equals(a.PublicKey, b.PublicKey, StringComparison.OrdinalIgnoreCase))
            return NodeIdentityMatch.None;
        if (string.IsNullOrEmpty(a.LongName)
            || !string.Equals(a.LongName, b.LongName, StringComparison.Ordinal))
            return NodeIdentityMatch.None;

        return a.HasDerivedNodeNumMatch != b.HasDerivedNodeNumMatch
            ? NodeIdentityMatch.PkiUpgrade
            : NodeIdentityMatch.None;
    }

    /// <summary>Which of two rows for one radio is the identity still on air.</summary>
    public static NodeRecord Survivor(NodeRecord a, NodeRecord b)
    {
        // The number the key derives is the one the radio answers to now,
        // whatever the clock says: neighbours that have not caught up go on
        // relaying the ghost, so it can be heard later than the real row.
        if (a.HasDerivedNodeNumMatch != b.HasDerivedNodeNumMatch)
            return a.HasDerivedNodeNumMatch ? a : b;
        return a.LastHeardEpoch >= b.LastHeardEpoch ? a : b;
    }
}

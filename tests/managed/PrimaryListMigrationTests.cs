// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;
using MeshRF.Messages;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The primary's channels used to live in a list with no name, beside a list
/// per preset for every other mesh — so the mesh the toolbar sat on was
/// described by two lists at once. They are one list now, named for the
/// preset, and the nameless one is folded into it on the next start.
/// </summary>
public class PrimaryListMigrationTests
{
    private static string TempDb() => Path.Combine(
        Path.GetTempPath(), "meshrf-tests", Guid.NewGuid().ToString("n"), "channels.db");

    private static ChannelConfig Channel(string preset, int index, string name, ChannelRole role) => new()
    {
        Preset = preset,
        Index = index,
        Name = name,
        Role = role,
        Psk = role == ChannelRole.Primary ? new byte[] { 0x01 } : ChannelConfig.NewRandomPsk(),
    };

    [Fact]
    public void TheNamelessListBecomesThePresetsAndKeepsItsOrder()
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var store = new ChannelStore(path);

        store.Upsert(Channel("", 0, "MediumFast", ChannelRole.Primary));
        store.Upsert(Channel("", 1, "Ham", ChannelRole.Secondary));
        store.Upsert(Channel("", 2, "Alta", ChannelRole.Secondary));

        Assert.Equal(3, store.MoveList(string.Empty, "MediumFast"));

        var moved = store.All().Where(c => c.Preset == "MediumFast").OrderBy(c => c.Index).ToList();
        Assert.Equal(new[] { "MediumFast", "Ham", "Alta" }, moved.Select(c => c.Name));
        Assert.Equal(new[] { 0, 1, 2 }, moved.Select(c => c.Index));
        Assert.DoesNotContain(store.All(), c => c.Preset.Length == 0);

        // Idempotent: there is no nameless list left, so a second start moves
        // nothing and cannot move a mesh twice.
        Assert.Equal(0, store.MoveList(string.Empty, "MediumFast"));
    }

    /// <summary>
    /// The target may already exist — a preset listened for as a secondary
    /// keeps a list of its own. The arriving channels are re-indexed past it
    /// rather than overwriting it, since (list, index) is the key and the two
    /// were numbered independently.
    /// </summary>
    [Fact]
    public void ChannelsArriveAfterWhateverTheTargetListAlreadyHolds()
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var store = new ChannelStore(path);

        store.Upsert(Channel("LongFast", 0, "LongFast", ChannelRole.Primary));
        store.Upsert(Channel("LongFast", 1, "club", ChannelRole.Secondary));
        store.Upsert(Channel("", 0, "LongFast", ChannelRole.Primary));
        store.Upsert(Channel("", 1, "Ham", ChannelRole.Secondary));

        Assert.Equal(2, store.MoveList(string.Empty, "LongFast"));

        var list = store.All().Where(c => c.Preset == "LongFast").OrderBy(c => c.Index).ToList();
        Assert.Equal(4, list.Count);
        Assert.Equal(new[] { "LongFast", "club", "LongFast", "Ham" }, list.Select(c => c.Name));
        Assert.Equal(new[] { 0, 1, 2, 3 }, list.Select(c => c.Index));
    }

    /// <summary>
    /// Both lists had a Primary of their own, and firmware allows a mesh one.
    /// The lowest index keeps it; the other becomes an ordinary secondary
    /// rather than a second Primary nothing would know which to believe.
    /// </summary>
    [Fact]
    public void TheMergedListEndsWithExactlyOnePrimary()
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var store = new ChannelStore(path);

        store.Upsert(Channel("LongFast", 0, "LongFast", ChannelRole.Primary));
        store.Upsert(Channel("", 0, "MediumFast", ChannelRole.Primary));
        store.Upsert(Channel("", 1, "Ham", ChannelRole.Secondary));

        store.MoveList(string.Empty, "LongFast");

        var list = store.All().Where(c => c.Preset == "LongFast").OrderBy(c => c.Index).ToList();
        var primary = Assert.Single(list, c => c.Role == ChannelRole.Primary);
        Assert.Equal("LongFast", primary.Name);
        Assert.Equal(0, primary.Index);
        Assert.Equal(ChannelRole.Secondary, list.Single(c => c.Name == "MediumFast").Role);
    }

    /// <summary>Nothing to move is not an error — that is what makes running
    /// this on every start harmless.</summary>
    [Fact]
    public void AnAlreadyMigratedStoreIsLeftAlone()
    {
        var path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var store = new ChannelStore(path);

        store.Upsert(Channel("MediumFast", 0, "MediumFast", ChannelRole.Primary));

        Assert.Equal(0, store.MoveList(string.Empty, "MediumFast"));
        Assert.Equal(0, store.MoveList("MediumFast", "MediumFast"));
        Assert.Single(store.All());
    }

    /// <summary>Stored packets move with the channels, or replayed history
    /// would land on a mesh that no longer has those tabs.</summary>
    [Fact]
    public void StoredPacketsAreRefiledOntoTheSameMesh()
    {
        var dir = Path.Combine(Path.GetTempPath(), "meshrf-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        using var messages = new MessageStore(Path.Combine(dir, "messages.db"));

        messages.Add(Record(1, preset: "", channel: "MediumFast"));
        messages.Add(Record(2, preset: "", channel: "Alta"));
        messages.Add(Record(3, preset: "LongFast", channel: "LongFast"));

        Assert.Equal(2, messages.MoveMesh(string.Empty, "MediumFast"));

        var history = messages.TextHistory();
        Assert.Equal(2, history.Count(m => m.Preset == "MediumFast"));
        Assert.Single(history, m => m.Preset == "LongFast");
        Assert.DoesNotContain(history, m => m.Preset.Length == 0);
    }

    private static MessageRecord Record(uint id, string preset, string channel) => new()
    {
        PacketId = id,
        FromNode = 0x1234u,
        ToNode = 0xFFFFFFFFu,
        Channel = channel,
        Preset = preset,
        PortNum = (int)Mesh.PortNum.TextMessage,
        Text = $"packet {id}",
        Decrypted = true,
        RxEpoch = 1_700_000_000 + id,
    };
}

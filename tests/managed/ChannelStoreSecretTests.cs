// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshRF.Tests;

public class ChannelStoreSecretTests : IDisposable
{
    private readonly string _dir;
    private readonly string _db;

    public ChannelStoreSecretTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meshrf-chan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "channels.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] Key(byte seed)
    {
        var key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
        return key;
    }

    private static ChannelConfig Channel(byte[] psk) => new()
    {
        Index = 0,
        Name = "Private",
        Psk = psk,
        Role = ChannelRole.Primary,
    };

    private byte[] RawPsk(int index)
    {
        using var conn = new SqliteConnection($"Data Source={_db}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT psk FROM channels WHERE idx = $i";
        cmd.Parameters.AddWithValue("$i", index);
        return (byte[])cmd.ExecuteScalar()!;
    }

    private void WriteRawPsk(int index, byte[] psk)
    {
        using var conn = new SqliteConnection($"Data Source={_db}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE channels SET psk = $psk WHERE idx = $i";
        cmd.Parameters.AddWithValue("$psk", psk);
        cmd.Parameters.AddWithValue("$i", index);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void AKeyIsNotStoredInTheClearButComesBackIntact()
    {
        var psk = Key(4);
        using (var store = new ChannelStore(_db))
        {
            store.Upsert(Channel(psk));
            Assert.Equal(psk, store.All().Single().Psk);
        }

        var raw = RawPsk(0);
        Assert.NotEqual(psk, raw);
        Assert.DoesNotContain(Convert.ToHexString(psk), Convert.ToHexString(raw));
    }

    [Fact]
    public void APlaintextKeyFromAnOlderBuildIsProtectedOnOpen()
    {
        var psk = Key(6);
        using (var store = new ChannelStore(_db)) store.Upsert(Channel(psk));

        // Put it back the way an older build wrote it.
        WriteRawPsk(0, psk);
        Assert.Equal(psk, RawPsk(0));

        using (var store = new ChannelStore(_db))
        {
            // Still readable, and no longer plaintext on disk.
            Assert.Equal(psk, store.All().Single().Psk);
        }
        Assert.NotEqual(psk, RawPsk(0));
    }

    [Fact]
    public void AKeyThatWillNotDecryptDisablesTheChannelRatherThanClearingIt()
    {
        // What a database copied to another machine looks like. Falling back to
        // an empty PSK would mean "no encryption" on a primary — this channel's
        // traffic in the clear — so it has to fail closed instead.
        using (var store = new ChannelStore(_db)) store.Upsert(Channel(Key(8)));

        var corrupt = RawPsk(0);
        corrupt[^1] ^= 0xFF;
        WriteRawPsk(0, corrupt);

        using var reopened = new ChannelStore(_db);
        var channel = reopened.All().Single();
        Assert.Equal(ChannelRole.Disabled, channel.Role);
        Assert.True(channel.IsDisabled);
        Assert.Empty(channel.Psk);
    }

    [Fact]
    public void TheDefaultKeyShorthandSurvivesTheRoundTrip()
    {
        using var store = new ChannelStore(_db);
        store.Upsert(Channel(new byte[] { 0x01 }));
        var channel = store.All().Single();
        Assert.Equal(new byte[] { 0x01 }, channel.Psk);
        Assert.True(channel.UsesDefaultKey);
    }

    [Fact]
    public void AnUnencryptedChannelStaysUnencrypted()
    {
        using var store = new ChannelStore(_db);
        store.Upsert(Channel(Array.Empty<byte>()));
        Assert.Empty(store.All().Single().Psk);
    }
}

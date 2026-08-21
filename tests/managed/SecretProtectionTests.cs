// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using MeshRF.Security;
using Xunit;

namespace MeshRF.Tests;

public class SecretProtectionTests : IDisposable
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MeshRF.Test.v1");
    private readonly string _dir;

    public SecretProtectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meshrf-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] Key(byte seed)
    {
        var key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
        return key;
    }

    [Fact]
    public void AKeyRoundTrips()
    {
        var plain = Key(7);
        var stored = SecretProtection.ProtectBytes(plain, Entropy, _dir);

        Assert.True(SecretProtection.IsProtected(stored));
        Assert.True(SecretProtection.TryUnprotectBytes(stored, Entropy, _dir, out var back));
        Assert.Equal(plain, back);
    }

    [Fact]
    public void TheStoredFormDoesNotContainThePlaintext()
    {
        // The whole point: a channels.db that leaves the machine must not have
        // the key sitting in it.
        var plain = Key(11);
        var stored = SecretProtection.ProtectBytes(plain, Entropy, _dir);
        Assert.DoesNotContain(Convert.ToHexString(plain), Convert.ToHexString(stored));
    }

    [Fact]
    public void EveryRealKeyLengthIsHandled()
    {
        foreach (int length in new[] { 1, 16, 32 })
        {
            var plain = Key(3).AsSpan(0, length).ToArray();
            var stored = SecretProtection.ProtectBytes(plain, Entropy, _dir);
            Assert.True(SecretProtection.TryUnprotectBytes(stored, Entropy, _dir, out var back));
            Assert.Equal(plain, back);
        }
    }

    [Fact]
    public void AnEmptyKeyStaysEmpty()
    {
        var stored = SecretProtection.ProtectBytes(Array.Empty<byte>(), Entropy, _dir);
        Assert.Empty(stored);
        Assert.True(SecretProtection.TryUnprotectBytes(stored, Entropy, _dir, out var back));
        Assert.Empty(back);
    }

    [Fact]
    public void LegacyPlaintextIsReadBackUnchanged()
    {
        // Databases written before this existed must keep working.
        var plain = Key(5);
        Assert.False(SecretProtection.IsProtected(plain));
        Assert.True(SecretProtection.TryUnprotectBytes(plain, Entropy, _dir, out var back));
        Assert.Equal(plain, back);
    }

    [Fact]
    public void NoRealKeyIsMistakenForAProtectedBlob()
    {
        // A stored value is only treated as protected if it is longer than any
        // key could be, so the marker can never collide with key bytes.
        for (int length = 0; length <= 32; length++)
            Assert.False(SecretProtection.IsProtected(new byte[length]));
    }

    [Fact]
    public void ATamperedBlobFailsRatherThanReturningSomething()
    {
        var stored = SecretProtection.ProtectBytes(Key(9), Entropy, _dir);
        stored[^1] ^= 0xFF;
        Assert.False(SecretProtection.TryUnprotectBytes(stored, Entropy, _dir, out var back));
        Assert.Empty(back);
    }

    [Fact]
    public void TextRoundTripsBothWays()
    {
        const string secret = "hunter2";
        var stored = SecretProtection.ProtectText(secret, Entropy, _dir, base64: false);
        Assert.Equal(secret, SecretProtection.UnprotectText(stored, Entropy, _dir, base64: false));

        var key = Convert.ToBase64String(Key(2));
        var storedKey = SecretProtection.ProtectText(key, Entropy, _dir, base64: true);
        Assert.Equal(key, SecretProtection.UnprotectText(storedKey, Entropy, _dir, base64: true));
    }
}

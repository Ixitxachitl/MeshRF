// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Security.Cryptography;
using MeshRF.Security;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The at-rest secret protection used on non-Windows platforms. The
/// key-explicit primitives are exercised directly so the crypto is provable on
/// any OS, and the Protect/Unprotect pair is run against a temp key directory
/// (the class itself is not platform-gated; only AppSettings' branch is).
/// </summary>
public class MachineBoundSecretTests
{
    private static byte[] Key(string seed = "a") =>
        MachineBoundSecret.DeriveKey($"machine-{seed}", $"user-{seed}", new byte[32]);

    [Theory]
    [InlineData("large4cats")]
    [InlineData("")]
    [InlineData("pässwörd with ünïcode 🔐 and\nnewlines")]
    public void EncryptWithKey_RoundTrips(string plain)
    {
        var key = Key();
        var stored = MachineBoundSecret.EncryptWithKey(plain, key);
        Assert.StartsWith("mrf1:", stored);
        Assert.Equal(plain, MachineBoundSecret.TryDecryptWithKey(stored, key));
    }

    [Fact]
    public void EncryptWithKey_ProducesUniqueCiphertexts()
    {
        var key = Key();
        // Random nonces: identical plaintexts must not produce identical
        // stored values, or the file would leak which secrets are equal.
        Assert.NotEqual(
            MachineBoundSecret.EncryptWithKey("same", key),
            MachineBoundSecret.EncryptWithKey("same", key));
    }

    [Fact]
    public void TryDecryptWithKey_WrongKey_ReturnsNull()
    {
        var stored = MachineBoundSecret.EncryptWithKey("secret", Key("a"));
        Assert.Null(MachineBoundSecret.TryDecryptWithKey(stored, Key("b")));
    }

    [Fact]
    public void TryDecryptWithKey_Tampered_ReturnsNull()
    {
        var key = Key();
        var stored = MachineBoundSecret.EncryptWithKey("secret", key);
        // Flip one ciphertext byte; GCM's tag must reject it.
        var packed = Convert.FromBase64String(stored["mrf1:".Length..]);
        packed[14] ^= 0xFF;
        var tampered = "mrf1:" + Convert.ToBase64String(packed);
        Assert.Null(MachineBoundSecret.TryDecryptWithKey(tampered, key));
    }

    [Theory]
    [InlineData("plaintext-password")]
    [InlineData("bXktYmFzZTY0LWtleQ==")]
    public void TryDecryptWithKey_NoPrefix_ReturnsNull(string legacy) =>
        Assert.Null(MachineBoundSecret.TryDecryptWithKey(legacy, Key()));

    [Fact]
    public void DeriveKey_IsStable_AndInputSensitive()
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var k1 = MachineBoundSecret.DeriveKey("machine", "user", salt);
        var k2 = MachineBoundSecret.DeriveKey("machine", "user", salt);
        Assert.Equal(k1, k2);

        Assert.NotEqual(k1, MachineBoundSecret.DeriveKey("other-machine", "user", salt));
        Assert.NotEqual(k1, MachineBoundSecret.DeriveKey("machine", "other-user", salt));
        Assert.NotEqual(k1, MachineBoundSecret.DeriveKey("machine", "user", RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void ProtectUnprotect_RoundTrips_WithRealSaltFile()
    {
        var dir = Directory.CreateTempSubdirectory("mrf-secret-test").FullName;
        try
        {
            var stored = MachineBoundSecret.Protect("large4cats", dir);
            Assert.StartsWith("mrf1:", stored);
            Assert.True(File.Exists(Path.Combine(dir, "secret.salt")));
            Assert.Equal("large4cats", MachineBoundSecret.Unprotect(stored, dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Unprotect_LegacyPlaintext_PassesThrough()
    {
        var dir = Directory.CreateTempSubdirectory("mrf-secret-test").FullName;
        try
        {
            // Pre-encryption settings files carry raw values; they must read
            // unchanged so nothing is lost on upgrade.
            Assert.Equal("old-plain-password", MachineBoundSecret.Unprotect("old-plain-password", dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Unprotect_PrefixedButUndecryptable_ReturnsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory("mrf-secret-test").FullName;
        try
        {
            // A blob from another machine/salt must yield empty (=> app
            // defaults), never ciphertext masquerading as a password.
            var foreign = MachineBoundSecret.EncryptWithKey("secret", Key("elsewhere"));
            Assert.Equal(string.Empty, MachineBoundSecret.Unprotect(foreign, dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

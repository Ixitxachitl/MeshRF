// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace MeshRF.Security;

/// <summary>
/// At-rest protection for secrets in settings.json on platforms without DPAPI
/// (Linux/macOS). AES-256-GCM under a key derived from the machine identity
/// and a per-user random salt file, so a settings.json that leaves the machine
/// — backups, copies, greps, a synced dotfiles repo — is unreadable ciphertext.
///
/// Be honest about what this is: containment, not DPAPI-grade protection. The
/// derivation is in this open-source file, so a local attacker who can read
/// the user's files can also read the inputs and derive the key. The Windows
/// path keeps real DPAPI; this narrows the exposure everywhere else from
/// "plaintext in a text file" to "must be decrypted on this machine, as this
/// user, with the salt file in hand".
///
/// Key inputs, in order of preference:
///  - /etc/machine-id (systemd standard; ties the key to the machine). macOS
///    has no such file and deliberately gets no native substitute — reading
///    IOPlatformUUID would mean IOKit interop inside settings load on a
///    platform this project cannot test.
///  - The username.
///  - A 32-byte random salt file beside settings.json, created on first use
///    with owner-only permissions. Copying it along with settings.json is
///    what makes a deliberate migration carry secrets over.
///
/// Wire format: "mrf1:" + base64(nonce || ciphertext || tag). Values without
/// the prefix are returned as-is (legacy plaintext keeps working; the next
/// save encrypts). A value with the prefix that fails to decrypt returns
/// empty — the app then falls back to its defaults — rather than handing
/// garbage to a broker login or key parser.
/// </summary>
public static class MachineBoundSecret
{
    private const string Prefix = "mrf1:";
    private const string SaltFileName = "secret.salt";
    private const int SaltBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int Pbkdf2Iterations = 100_000;

    private static readonly object KeyLock = new();
    private static byte[]? _cachedKey;
    private static string? _cachedKeyDir;

    /// <summary>Encrypts <paramref name="plain"/> for storage. Empty in, empty out.</summary>
    public static string Protect(string plain, string keyDir)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;
        try
        {
            return EncryptWithKey(plain, GetKey(keyDir));
        }
        catch
        {
            // Same best-effort stance as the DPAPI path: a failure to protect
            // must never lose the secret itself.
            return plain;
        }
    }

    /// <summary>Decrypts a stored value. No prefix means legacy plaintext and is
    /// returned unchanged; a prefixed value that fails to decrypt (moved
    /// machine, missing salt file, tampering) returns empty.</summary>
    public static string Unprotect(string stored, string keyDir)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        try
        {
            return TryDecryptWithKey(stored, GetKey(keyDir)) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // -- Primitives, key-explicit so tests can exercise them on any OS -------

    /// <summary>AES-256-GCM encrypt to the "mrf1:" wire format.</summary>
    public static string EncryptWithKey(string plain, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var packed = new byte[NonceBytes + cipher.Length + TagBytes];
        nonce.CopyTo(packed, 0);
        cipher.CopyTo(packed, NonceBytes);
        tag.CopyTo(packed, NonceBytes + cipher.Length);
        return Prefix + Convert.ToBase64String(packed);
    }

    /// <summary>Decrypts the "mrf1:" wire format, or null when the value is
    /// malformed, tampered with, or encrypted under a different key.</summary>
    public static string? TryDecryptWithKey(string stored, byte[] key)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var packed = Convert.FromBase64String(stored[Prefix.Length..]);
            if (packed.Length < NonceBytes + TagBytes) return null;

            var nonce = packed.AsSpan(0, NonceBytes);
            var cipher = packed.AsSpan(NonceBytes, packed.Length - NonceBytes - TagBytes);
            var tag = packed.AsSpan(packed.Length - TagBytes);

            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>PBKDF2 over machine-id + username + salt. Public so tests can
    /// verify derivation stability with fixed inputs.</summary>
    public static byte[] DeriveKey(string machineId, string userName, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(machineId + "\n" + userName),
            salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, outputLength: 32);

    // -- Key material --------------------------------------------------------

    private static byte[] GetKey(string keyDir)
    {
        lock (KeyLock)
        {
            if (_cachedKey is not null && _cachedKeyDir == keyDir) return _cachedKey;
            _cachedKey = DeriveKey(ReadMachineId(), Environment.UserName, LoadOrCreateSalt(keyDir));
            _cachedKeyDir = keyDir;
            return _cachedKey;
        }
    }

    private static string ReadMachineId()
    {
        // Absent on macOS and some containers; the salt file and username
        // still key the encryption there, just without the machine binding.
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            try
            {
                if (File.Exists(path)) return File.ReadAllText(path).Trim();
            }
            catch { /* unreadable is the same as absent */ }
        }
        return string.Empty;
    }

    private static byte[] LoadOrCreateSalt(string keyDir)
    {
        var path = Path.Combine(keyDir, SaltFileName);
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length == SaltBytes) return existing;
            }
        }
        catch { /* fall through to recreate */ }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        Directory.CreateDirectory(keyDir);
        File.WriteAllBytes(path, salt);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* exotic filesystems may refuse; the salt still works */ }
        }
        return salt;
    }
}

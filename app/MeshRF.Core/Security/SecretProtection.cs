// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace MeshRF.Security;

/// <summary>
/// At-rest protection for the app's secrets, on whichever mechanism the
/// platform offers: DPAPI (CurrentUser) on Windows, <see cref="MachineBoundSecret"/>
/// elsewhere.
/// </summary>
/// <remarks>
/// The entropy values callers pass are not secrets — they scope a protected
/// blob to one kind of secret in one app, so a stored MQTT password cannot be
/// fed to the private-key parser.
///
/// Every read treats an unrecognised stored value as legacy plaintext, so
/// settings and databases written before this existed keep working and are
/// protected on the next save.
/// </remarks>
public static class SecretProtection
{
    /// <summary>Marks a protected byte blob. Its absence means the value
    /// predates protection and is stored as-is.</summary>
    /// <remarks>
    /// Needed only for the byte form: a protected blob is not distinguishable
    /// from arbitrary bytes without one. The length check alongside it is what
    /// makes a false positive impossible rather than merely unlikely — every
    /// secret stored this way is far shorter than any blob protecting it.
    /// </remarks>
    private static readonly byte[] BlobMagic = "MRFsec1"u8.ToArray();

    /// <summary>Longest plaintext the byte form is used for (a 32-byte AES
    /// key). Anything at or under this cannot be a protected blob.</summary>
    private const int MaxPlainBytes = 32;

    public static string ProtectText(string plain, byte[] entropy, string keyDir, bool base64)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;
        if (!OperatingSystem.IsWindows()) return MachineBoundSecret.Protect(plain, keyDir);
        try
        {
            var bytes = base64 ? Convert.FromBase64String(plain) : Encoding.UTF8.GetBytes(plain);
            return Convert.ToBase64String(ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            // Same stance as everywhere else here: failing to protect must
            // never lose the secret itself.
            return plain;
        }
    }

    public static string UnprotectText(string stored, byte[] entropy, string keyDir, bool base64)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (!OperatingSystem.IsWindows()) return MachineBoundSecret.Unprotect(stored, keyDir);
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored), entropy,
                                                DataProtectionScope.CurrentUser);
            return base64 ? Convert.ToBase64String(bytes) : Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Not a blob of ours (legacy plaintext, or another user/machine).
            return stored;
        }
    }

    /// <summary>Protects a short binary secret — a channel key. Returns the
    /// input unchanged if protection is unavailable, so the secret survives.</summary>
    public static byte[] ProtectBytes(byte[] plain, byte[] entropy, string keyDir)
    {
        if (plain.Length == 0) return plain;
        try
        {
            byte[] body = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser)
                : Encoding.UTF8.GetBytes(MachineBoundSecret.Protect(Convert.ToBase64String(plain), keyDir));

            var result = new byte[BlobMagic.Length + body.Length];
            BlobMagic.CopyTo(result, 0);
            body.CopyTo(result, BlobMagic.Length);
            return result;
        }
        catch
        {
            return plain;
        }
    }

    /// <summary>True if this stored value carries the protection marker.</summary>
    public static bool IsProtected(byte[] stored) =>
        stored.Length > MaxPlainBytes && stored.AsSpan(0, BlobMagic.Length).SequenceEqual(BlobMagic);

    /// <summary>
    /// Recovers a protected binary secret. Unprotected input is legacy
    /// plaintext and comes back unchanged; input that will not decrypt returns
    /// false, and the caller must treat the secret as lost rather than guess.
    /// </summary>
    public static bool TryUnprotectBytes(byte[] stored, byte[] entropy, string keyDir, out byte[] plain)
    {
        if (!IsProtected(stored))
        {
            plain = stored;
            return true;
        }

        try
        {
            var body = stored.AsSpan(BlobMagic.Length).ToArray();
            plain = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(body, entropy, DataProtectionScope.CurrentUser)
                : Convert.FromBase64String(MachineBoundSecret.Unprotect(Encoding.UTF8.GetString(body), keyDir));
            return true;
        }
        catch
        {
            plain = Array.Empty<byte>();
            return false;
        }
    }
}

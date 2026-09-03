using System;
using System.Security.Cryptography;
using System.Text;

namespace ProxyDivert.Core.Configuration;

// Encrypts proxy passwords with DPAPI before they are written to the config file.
//
// Scope is CurrentUser: the file is only readable by the Windows account that wrote it, which is
// the right trade-off for a desktop tool — no key to manage, and copying the file to another
// machine yields nothing. It is NOT protection against code running as that same user.
//
// Values are stored with a marker prefix so a config file that predates encryption, or one a user
// edited by hand, still loads: anything without the prefix is taken as clear text.
public static class SecretProtector
{
    private const string Prefix = "dpapi:";

    // Ties the ciphertext to this application, so a blob lifted from another program's config
    // cannot be decrypted here (and vice versa).
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ProxyDivert.Config.v1");

    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;
        try
        {
            byte[] cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plainText!), Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(cipher);
        }
        catch (CryptographicException)
        {
            // Rather than lose the credential, store it as-is: the user asked for it to be saved.
            return plainText;
        }
    }

    public static string? Unprotect(string? storedValue)
    {
        if (string.IsNullOrEmpty(storedValue)) return storedValue;
        if (!storedValue!.StartsWith(Prefix, StringComparison.Ordinal)) return storedValue;

        try
        {
            byte[] cipher = Convert.FromBase64String(storedValue.Substring(Prefix.Length));
            byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Wrong user account, or a corrupted file. The password is simply gone — returning
            // null makes the outbound fail to authenticate visibly instead of half-working.
            return null;
        }
    }

    public static bool IsProtected(string? storedValue)
        => storedValue != null && storedValue.StartsWith(Prefix, StringComparison.Ordinal);
}

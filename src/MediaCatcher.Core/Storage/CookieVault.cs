using System.Security.Cryptography;
using System.Text;

namespace MediaCatcher.Core.Storage;

public static class CookieVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MediaCatcher.Cookies.v1");

    public static byte[]? Protect(string? cookies)
    {
        if (string.IsNullOrWhiteSpace(cookies))
        {
            return null;
        }

        var plain = Encoding.UTF8.GetBytes(cookies);
        return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    }

    public static string? Unprotect(byte[]? protectedCookies)
    {
        if (protectedCookies is null || protectedCookies.Length == 0)
        {
            return null;
        }

        try
        {
            var plain = ProtectedData.Unprotect(protectedCookies, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using ShieldLabs.Internal;

namespace ShieldLabs;

/// <summary>Creates a User HID on your server.</summary>
public static class UserHid
{
    /// <summary>
    /// Returns HMAC-SHA256(key = <paramref name="secret"/>, message = <paramref name="userId"/>) as
    /// 64 lowercase hex characters: a stable, irreversible User HID to pass to the browser agent
    /// instead of a raw email address or account ID.
    /// </summary>
    /// <param name="userId">Your internal account identifier.</param>
    /// <param name="secret">A server-side secret used only for this purpose. Keep it stable: changing it changes every User HID.</param>
    /// <exception cref="ValidationException"><paramref name="userId"/> or <paramref name="secret"/> is null or empty.</exception>
    public static string FromUserId(string userId, string secret)
    {
        if (string.IsNullOrEmpty(userId))
        {
            throw new ValidationException("userId must be a non-empty string.");
        }

        if (string.IsNullOrEmpty(secret))
        {
            throw new ValidationException("secret must be a non-empty string.");
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Hex.ToLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(userId)));
    }
}

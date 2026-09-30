using System;
using System.Runtime.CompilerServices;

namespace ShieldLabs.Internal;

internal static class Hex
{
    private const string LowerDigits = "0123456789abcdef";

    /// <summary>Lowercase hexadecimal encoding.</summary>
    internal static string ToLower(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = LowerDigits[bytes[i] >> 4];
            chars[(i * 2) + 1] = LowerDigits[bytes[i] & 0xF];
        }

        return new string(chars);
    }

    /// <summary>Decodes hexadecimal text (either case). Returns false for odd lengths or non-hex characters.</summary>
    internal static bool TryDecode(string text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (text.Length % 2 != 0)
        {
            return false;
        }

        var result = new byte[text.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            var high = Nibble(text[i * 2]);
            var low = Nibble(text[(i * 2) + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            result[i] = (byte)((high << 4) | low);
        }

        bytes = result;
        return true;
    }

    /// <summary>Compares two byte arrays in time that depends only on their length.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static bool FixedTimeEquals(byte[] left, byte[] right)
    {
#if NET8_0_OR_GREATER
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
#else
        if (left.Length != right.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < left.Length; i++)
        {
            diff |= left[i] ^ right[i];
        }

        return diff == 0;
#endif
    }

    private static int Nibble(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }

        if (c >= 'a' && c <= 'f')
        {
            return c - 'a' + 10;
        }

        if (c >= 'A' && c <= 'F')
        {
            return c - 'A' + 10;
        }

        return -1;
    }
}

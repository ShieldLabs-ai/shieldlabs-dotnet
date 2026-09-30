using System.Globalization;
using System.Text;

namespace ShieldLabs.Internal;

/// <summary>Text helpers with the whitespace, letter and case rules the signal slug function relies on.</summary>
internal static class PyText
{
    /// <summary>Whitespace test: Unicode white space plus the information separators U+001C to U+001F.</summary>
    internal static bool IsSpace(char c) => char.IsWhiteSpace(c) || (c >= '\u001C' && c <= '\u001F');

    /// <summary>Removes leading and trailing whitespace (see <see cref="IsSpace"/>).</summary>
    internal static string Strip(string text)
    {
        var start = 0;
        var end = text.Length - 1;
        while (start <= end && IsSpace(text[start]))
        {
            start++;
        }

        while (end >= start && IsSpace(text[end]))
        {
            end--;
        }

        return start == 0 && end == text.Length - 1 ? text : text.Substring(start, end - start + 1);
    }

    /// <summary>
    /// Appends the lowercase form of the code point at <paramref name="index"/> when it is a letter
    /// (any L category) or a decimal digit, and returns the number of UTF-16 units consumed.
    /// </summary>
    internal static int AppendLowerLetterOrDigit(string text, int index, StringBuilder output, out bool appended)
    {
        var length = char.IsSurrogatePair(text, index) ? 2 : 1;
        var category = CharUnicodeInfo.GetUnicodeCategory(text, index);
        appended = false;
        switch (category)
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.DecimalDigitNumber:
                output.Append(text.Substring(index, length).ToLowerInvariant());
                appended = true;
                break;
        }

        return length;
    }
}

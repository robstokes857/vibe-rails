using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Tests;

/// <summary>
/// New cards carry a stored key with a random middle part (PROJ-X7K2Q-1, VB-51). Tests that are
/// about something other than the key compare the stable short form (PROJ-1) instead.
/// A null (a JSON property that is absent or null) passes through so the caller's assertion
/// reports the missing value instead of an ArgumentNullException from the regex.
/// </summary>
internal static partial class BoardKeyText
{
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Short(string? text) => text is null ? null : StoredKey().Replace(text, "$1-$2");

    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9]{0,7})-[A-Z0-9]{5}-(\d+)\b")]
    private static partial Regex StoredKey();
}

namespace VibeRails.Services.Board;

/// <summary>Display labels are independent of immutable Board keys.</summary>
public static class BoardDisplayIds
{
    /// <summary>First four usable characters of the repository folder, padded for short names.</summary>
    public static string DefaultPrefix(string projectPath)
    {
        var name = projectPath.TrimEnd('/', '\\').Replace('\\', '/').Split('/').Last();
        var letters = new string(name.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (letters.Length == 0 || !char.IsAsciiLetter(letters[0])) letters = "REPO" + letters;
        return letters.PadRight(4, 'X')[..4];
    }

    /// <summary>Normalize a board's configurable prefix, or reject invalid input.</summary>
    public static string NormalizePrefix(string value)
    {
        var prefix = value.Trim().ToUpperInvariant();
        if (prefix.Length is < 1 or > 8 || !char.IsAsciiLetter(prefix[0]) || !prefix.All(char.IsAsciiLetterOrDigit))
            throw new BoardValidationException("Display ID prefix must be 1–8 letters or digits, starting with a letter.");
        return prefix;
    }

    /// <summary>Accept sequential labels and preserved legacy immutable keys.</summary>
    public static string Normalize(string value)
    {
        if (BoardKeys.TryParse(value, out var prefix, out var number)) return BoardKeys.Format(prefix, number);
        if (BoardKeys.TryParseStored(value, out var key)) return key;
        throw new BoardValidationException("Display ID must be a prefix and positive number, such as VIBE-123.");
    }
}

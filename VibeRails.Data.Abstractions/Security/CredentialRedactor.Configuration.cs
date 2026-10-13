using System.Text.RegularExpressions;

namespace VibeRails.Security;

public static partial class CredentialRedactor
{
    /// <summary>Whether a configuration property names a credential, ignoring case, underscores and hyphens.</summary>
    public static bool IsCredentialProperty(string name) => IsNormalizedCredentialProperty(NormalizeName(name));

    private static bool IsNormalizedCredentialProperty(string name) =>
        name.Contains("apikey") || name.Contains("password") || name.Contains("passwd") || name.Contains("secret")
        || name.Contains("credential") || name.Contains("privatekey") || name.Contains("accesskey") || name.Contains("cookie")
        || name.Contains("authorization") || name.Contains("bearer") || name.Contains("sessionid") || name.Contains("sessionkey")
        || name.EndsWith("token") || name.EndsWith("auth")
        // OPENAI_KEY, STRIPE_SECRET_KEY, GITHUB_PAT; a bare "key" is a keystroke in keybinding files.
        || (name.Length > 3 && (name.EndsWith("key") || name.EndsWith("pat")))
        || name is "tokens" or "pinhash" or "pinsalt" or "remoteaccountemail" or "remoteaccountkeyfingerprint";

    private static string NormalizeName(string name) => name.Replace("_", "").Replace("-", "").ToLowerInvariant();

    /// <summary>Stands in for a credential inside a string or list, where dropping it would change the shape around it.</summary>
    public const string ConfigurationRedactionMarker = "[redacted]";

    /// <summary>
    /// A string list (MCP <c>args</c>, a command array): each item through <see cref="RedactConfigurationValue"/>, plus the item
    /// after a credential-named flag (<c>"--api-key", "sk-…"</c>) or an auth scheme (<c>"Bearer", "…"</c>), and the
    /// value carried by a header/environment flag (<c>"--header", "X-Deployment: …"</c>, <c>"-e", "NAME=…"</c>).
    /// </summary>
    public static List<string> RedactArguments(IReadOnlyList<string> items)
    {
        var result = new List<string>(items.Count);
        var next = NextWord.Ordinary;
        foreach (var item in items)
        {
            if (next != NextWord.Ordinary && !item.StartsWith('-')) { result.Add(RedactCarried(item, next)); next = NextWord.Ordinary; continue; }
            next = NextWordAfter(item.Trim());
            result.Add(RedactConfigurationValue(item));
        }
        return result;
    }

    /// <summary>What the word after a flag or scheme is: ordinary, a hidden credential, a header, or an environment assignment.</summary>
    private enum NextWord { Ordinary, Hidden, Header, Environment }

    private static NextWord NextWordAfter(string word)
    {
        var (kind, valueOffset) = Carrier(word);
        if (kind != NextWord.Ordinary) return valueOffset < 0 ? kind : NextWord.Ordinary;
        return HidesNextWord(word) ? NextWord.Hidden : NextWord.Ordinary;
    }

    /// <summary>
    /// A word that is a carrier flag: alone (value offset -1; the next word is the value) or with its value attached,
    /// as <c>--header=Name: …</c>, <c>--env=NAME=…</c> or the short forms <c>-HName: …</c> and <c>-eNAME=…</c>.
    /// </summary>
    private static (NextWord Kind, int ValueOffset) Carrier(string word)
    {
        if (word.Length > 2 && word[0] == '-' && word[1] != '-' && CarrierKind(word[..2]) is var attached && attached != NextWord.Ordinary) return (attached, 2);
        var equals = word.IndexOf('=');
        var kind = CarrierKind(equals < 0 ? word : word[..equals]);
        return kind == NextWord.Ordinary ? (NextWord.Ordinary, -1) : (kind, equals < 0 ? -1 : equals + 1);
    }

    /// <summary>
    /// A flag whose value is a header (<c>--header</c>, <c>-H</c>) or an environment assignment (<c>--env</c>, <c>-e</c>).
    /// As in the <c>headers</c>/<c>env</c> blocks, the name inside is arbitrary and the value is a credential.
    /// </summary>
    private static NextWord CarrierKind(string flag)
    {
        if (flag == "-H") return NextWord.Header;
        if (flag == "-e") return NextWord.Environment;
        if (!flag.StartsWith("--", StringComparison.Ordinal)) return NextWord.Ordinary;
        return NormalizeName(flag[2..]) switch
        {
            "header" or "headers" or "httpheader" or "httpheaders" => NextWord.Header,
            "env" or "environment" => NextWord.Environment,
            _ => NextWord.Ordinary,
        };
    }

    /// <summary>
    /// A carried value keeps its name and loses its value (<c>X-Deployment:[redacted]</c>, <c>NAME=[redacted]</c>), so
    /// a restore shows what to re-enter. A header without a name is replaced whole; an environment name without a
    /// value (<c>-e NAME</c>, passed through from the host) carries nothing.
    /// </summary>
    private static string RedactCarried(string text, NextWord kind)
    {
        if (kind == NextWord.Hidden) return ConfigurationRedactionMarker;
        var separator = text.IndexOf(kind == NextWord.Header ? ':' : '=');
        if (separator >= 0) return text[..(separator + 1)] + ConfigurationRedactionMarker;
        return kind == NextWord.Header ? ConfigurationRedactionMarker : RedactConfigurationValue(text);
    }

    /// <summary>
    /// Every JSON/TOML string value: URL passwords; the value of any <c>name=value</c> or <c>Name: value</c> pair whose
    /// name is a credential, however deeply nested (<c>--header=Authorization: Bearer …</c>, <c>--env=API_KEY=…</c>,
    /// <c>…/sse?a=1&amp;token=…</c>); the value carried by a header/environment flag whatever its name
    /// (<c>--header X-Deployment: …</c>, <c>--env=NAME=…</c>); and inside space-separated text, the word after a
    /// credential flag or auth scheme (<c>server --api-key sk-…</c>). Everything after a credential separator is
    /// replaced, never partially kept.
    /// </summary>
    public static string RedactConfigurationValue(string value)
    {
        var text = RedactPlainText(value);
        // A percent-encoded pair (api_key%3D…) is invisible to the scan; if decoding reveals one, none of the value is kept.
        if (text.Contains('%'))
        {
            var decoded = Uri.UnescapeDataString(text);
            if (decoded != text && RedactPlainText(decoded) != decoded) return ConfigurationRedactionMarker;
        }
        return text;
    }

    private static string RedactPlainText(string value)
    {
        var text = UrlPassword().Replace(value, ConfigurationRedactionMarker);
        var cut = Math.Min(AssignmentCut(text), CarrierCut(text));
        if (cut < text.Length) text = text[..cut] + ConfigurationRedactionMarker;
        var words = WordSeparators().Split(text); // separators are kept at odd indexes
        if (words.Length == 1) return text;
        for (int i = 0, hidden = 0; i < words.Length; i += 2)
        {
            if (words[i].Length == 0) continue;
            if (hidden == 1 && !words[i].StartsWith('-'))
            {
                // A quoted value ("prefix secret") is one value to the end of the string unless its own word closes it.
                var last = QuotedValueEnd(words, i);
                words[i] = ConfigurationRedactionMarker;
                for (var j = i + 1; j <= last; j++) words[j] = "";
                i = last;
                hidden = 0;
                continue;
            }
            hidden = HidesNextWord(words[i]) ? 1 : 0;
        }
        return string.Concat(words);
    }

    /// <summary>
    /// Index of the word that closes the value starting at <paramref name="start"/>: the word itself when it opens no
    /// quote, or opens and closes one unescaped; otherwise the last word. Shell quoting inside a configuration string
    /// (escaped quotes, nested quotes) is not parsed, so the rest of the string is redacted rather than guessed at.
    /// </summary>
    private static int QuotedValueEnd(string[] words, int start)
    {
        var word = words[start];
        var quote = word[0];
        if (quote is not ('"' or '\'')) return start;
        if (word.Length >= 2 && word[^1] == quote && !IsEscaped(word, word.Length - 1)) return start;
        return words.Length - 1;
    }

    /// <summary>True when the character at <paramref name="index"/> is preceded by an odd run of backslashes.</summary>
    private static bool IsEscaped(string text, int index)
    {
        var backslashes = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--) backslashes++;
        return backslashes % 2 == 1;
    }

    /// <summary>Index just past the first <c>=</c>/<c>:</c> whose name is a credential, or the text's length.</summary>
    private static int AssignmentCut(string text)
    {
        for (var start = 0; ;)
        {
            var separator = text.IndexOfAny(AssignmentSeparators, start);
            if (separator < 0) return text.Length;
            var segment = text[start..separator];
            if (IsCredentialName(segment[(segment.LastIndexOfAny(WhitespaceCharacters) + 1)..])) return separator + 1;
            start = separator + 1;
        }
    }

    /// <summary>
    /// Index where the value carried by a header/environment flag begins (<c>--header X-Deployment: …</c>,
    /// <c>-e NAME=…</c>, <c>--header=Name: …</c>, <c>-HName: …</c>), or the text's length. The name's separator is looked for in the
    /// value's first word: a header without one is a value from its first character, a bare environment name is kept.
    /// </summary>
    private static int CarrierCut(string text)
    {
        var next = NextWord.Ordinary;
        var position = 0;
        foreach (var word in WordSeparators().Split(text))
        {
            var start = position;
            position += word.Length;
            if (word.Length == 0 || char.IsWhiteSpace(word[0])) continue;
            if (next != NextWord.Ordinary && !word.StartsWith('-'))
            {
                var cut = CarriedCut(text, start, word.Length, next);
                if (cut >= 0) return cut;
                next = NextWord.Ordinary;
                continue;
            }
            var (kind, valueOffset) = Carrier(word);
            if (kind == NextWord.Ordinary) { next = NextWord.Ordinary; continue; }
            if (valueOffset < 0) { next = kind; continue; }
            next = NextWord.Ordinary;
            var attached = CarriedCut(text, start + valueOffset, word.Length - valueOffset, kind);
            if (attached >= 0) return attached;
        }
        return text.Length;
    }

    /// <summary>Where a carried value starts within its word: after the name's <c>:</c>/<c>=</c>, at the word for a nameless header, -1 for a bare environment name.</summary>
    private static int CarriedCut(string text, int start, int length, NextWord kind)
    {
        var separator = text.IndexOf(kind == NextWord.Header ? ':' : '=', start, length);
        if (separator >= 0) return separator + 1;
        return kind == NextWord.Header ? start : -1;
    }

    private static bool HidesNextWord(string word) =>
        (word.StartsWith('-') && !word.Contains('=') && IsCredentialName(word))
        || word.Equals("bearer", StringComparison.OrdinalIgnoreCase) || word.Equals("basic", StringComparison.OrdinalIgnoreCase);

    /// <summary>A flag, query or assignment name that is a credential as written or once percent-decoded (<c>api%5Fkey</c>).</summary>
    private static bool IsCredentialName(string name)
    {
        var bare = name.TrimStart('-');
        if (bare.Length == 0) return false;
        if (IsCredentialProperty(NormalizeName(bare))) return true;
        return bare.Contains('%') && IsCredentialProperty(NormalizeName(Uri.UnescapeDataString(bare).TrimStart('-')));
    }

    private static readonly char[] AssignmentSeparators = ['=', ':'];
    private static readonly char[] WhitespaceCharacters = [' ', '\t', '\r', '\n'];

    [GeneratedRegex(@"(?<=://[^/@\s:]+:)[^/@\s]+(?=@)")]
    private static partial Regex UrlPassword();

    [GeneratedRegex(@"(\s+)")]
    private static partial Regex WordSeparators();
}

using System.Text;

namespace TerminalEmulator;

/// <summary>
/// Attribute flags packed into a single byte. AOT-safe (no reflection).
/// </summary>
[Flags]
public enum CellAttributes : byte
{
    None      = 0,
    Bold      = 1 << 0,
    Dim       = 1 << 1,
    Italic    = 1 << 2,
    Underline = 1 << 3,
    Blink     = 1 << 4,
    Inverse   = 1 << 5,
    Invisible = 1 << 6,
    Strike    = 1 << 7,
}

/// <summary>
/// Color representation. Supports default, 256-palette, and 24-bit true color.
/// Stored as a 32-bit int: top byte = kind, lower 3 bytes = value.
///   kind 0 = default terminal color
///   kind 1 = 256-palette index (low byte)
///   kind 2 = 24-bit RGB packed in low 3 bytes
/// </summary>
public readonly struct CellColor : IEquatable<CellColor>
{
    private readonly uint _value;

    public static readonly CellColor Default = new(0);

    private CellColor(uint value) => _value = value;

    public static CellColor FromPalette(byte index) =>
        new((uint)(1 << 24) | index);

    public static CellColor FromRgb(byte r, byte g, byte b) =>
        new((uint)(2 << 24) | (uint)(r << 16) | (uint)(g << 8) | b);

    public bool IsDefault => (_value >> 24) == 0;
    public bool IsPalette => (_value >> 24) == 1;
    public bool IsRgb    => (_value >> 24) == 2;

    public byte PaletteIndex => (byte)(_value & 0xFF);
    public byte R => (byte)((_value >> 16) & 0xFF);
    public byte G => (byte)((_value >> 8)  & 0xFF);
    public byte B => (byte)(_value & 0xFF);

    public bool Equals(CellColor other) => _value == other._value;
    public override bool Equals(object? obj) => obj is CellColor c && Equals(c);
    public override int GetHashCode() => (int)_value;
    public static bool operator ==(CellColor a, CellColor b) => a._value == b._value;
    public static bool operator !=(CellColor a, CellColor b) => a._value != b._value;
}

/// <summary>
/// A single terminal cell. Struct — no heap allocation per cell.
/// Wide characters (CJK/emoji) occupy 2 columns; the continuation cell has IsWide=true, Char='\0'.
/// </summary>
public struct TerminalCell
{
    /// <summary>The character at this cell. '\0' means empty/continuation.</summary>
    public char Char;

    /// <summary>
    /// Full Unicode scalar value for this cell. For BMP text this matches <see cref="Char"/>.
    /// For supplementary-plane glyphs, <see cref="Char"/> stores the lead surrogate while
    /// <see cref="Codepoint"/> preserves the full scalar for replay/serialization.
    /// </summary>
    public int Codepoint;

    /// <summary>Foreground color.</summary>
    public CellColor Fg;

    /// <summary>Background color.</summary>
    public CellColor Bg;

    /// <summary>Visual attributes (bold, underline, etc.).</summary>
    public CellAttributes Attributes;

    /// <summary>True if this is the right half of a 2-column wide character.</summary>
    public bool IsWideContinuation;

    public static readonly TerminalCell Empty = new()
    {
        Char = ' ',
        Codepoint = ' ',
        Fg = CellColor.Default,
        Bg = CellColor.Default,
        Attributes = CellAttributes.None,
        IsWideContinuation = false,
    };

    public readonly bool IsEmpty =>
        Codepoint == ' ' && Fg.IsDefault && Bg.IsDefault && Attributes == CellAttributes.None;

    public readonly void AppendText(StringBuilder builder, bool replaceControlWithSpace = false)
    {
        if (IsWideContinuation)
            return;

        var scalar = Codepoint != 0 ? Codepoint : Char;
        if (scalar == 0)
        {
            builder.Append(' ');
            return;
        }

        if (replaceControlWithSpace && scalar < ' ')
        {
            builder.Append(' ');
            return;
        }

        if (scalar <= char.MaxValue)
        {
            builder.Append((char)scalar);
            return;
        }

        builder.Append(char.ConvertFromUtf32(scalar));
    }
}

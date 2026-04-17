namespace VibeRails.Services.Terminal;

/// <summary>
/// Common ANSI/control sequence types seen in PTY streams.
/// This is not exhaustive, but covers the most common terminal traffic.
/// </summary>
public enum TerminalControlType
{
    None = 0,
    Text,

    // C0 controls
    Null,
    Bell,
    Backspace,
    HorizontalTab,
    LineFeed,
    VerticalTab,
    FormFeed,
    CarriageReturn,
    ShiftOut,
    ShiftIn,
    Escape,
    Delete,
    DeviceControl1,
    DeviceControl2,
    DeviceControl3,
    DeviceControl4,
    C0Control,

    // Escape sequence families
    CsiSequence,
    OscSequence,
    DcsSequence,
    ApcSequence,
    PmSequence,
    SosSequence,
    StringTerminator,
    CharsetDesignation,

    // Common CSI commands
    CursorUp,
    CursorDown,
    CursorForward,
    CursorBack,
    CursorNextLine,
    CursorPreviousLine,
    CursorHorizontalAbsolute,
    CursorPosition,
    EraseInDisplay,
    EraseInLine,
    InsertLine,
    DeleteLine,
    InsertCharacter,
    DeleteCharacter,
    ScrollUp,
    ScrollDown,
    SelectGraphicRendition,
    DeviceStatusReport,
    SetMode,
    ResetMode,
    SaveCursor,
    RestoreCursor,
    FocusIn,
    FocusOut,

    // Common ESC commands
    DecSaveCursor,
    DecRestoreCursor,
    Index,
    NextLine,
    ReverseIndex,
    FullReset,
    ApplicationKeypad,
    NormalKeypad,

    // Common OSC commands
    OscSetIconNameAndTitle,
    OscSetIconName,
    OscSetWindowTitle,
    OscSetWorkingDirectory,
    OscHyperlink,
    OscClipboard
}

/// <summary>
/// One parsed segment of terminal text.
/// </summary>
public readonly record struct TerminalTextWithControlPart(
    string Raw,
    string PlainText,
    bool IsControl,
    TerminalControlType ControlType);

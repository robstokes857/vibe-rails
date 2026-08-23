using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

public class KeyTranslatorTests
{
    [Fact]
    public void TranslateKey_ShiftEnter_DefaultMode_RemainsCarriageReturn()
    {
        var key = EnterKey(shift: true);

        var result = KeyTranslator.TranslateKey(key);

        Assert.Equal("\r", result);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TranslateKey_ModifiedEnter_LineFeedMode_UsesLineFeed(bool shift, bool control)
    {
        var key = EnterKey(shift, control);

        var result = KeyTranslator.TranslateKey(key, modifiedEnterAsLineFeed: true);

        Assert.Equal("\n", result);
    }

    [Fact]
    public void TranslateKey_PlainEnter_LineFeedMode_RemainsCarriageReturn()
    {
        var result = KeyTranslator.TranslateKey(
            EnterKey(shift: false),
            modifiedEnterAsLineFeed: true);

        Assert.Equal("\r", result);
    }

    private static ConsoleKeyInfo EnterKey(bool shift, bool control = false) =>
        new(
            keyChar: '\r',
            key: ConsoleKey.Enter,
            shift: shift,
            alt: false,
            control: control);
}

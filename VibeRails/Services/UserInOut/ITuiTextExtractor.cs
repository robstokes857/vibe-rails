namespace VibeRails.Services.UserInOut;

public interface ITuiTextExtractor
{
    string Extract(string rawInput, string tuiText);
}

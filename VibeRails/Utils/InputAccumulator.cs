using System.Text;

namespace VibeRails.Utils;

/// <summary>
/// Accumulates keystrokes from the terminal and fires a callback when the user presses Enter.
/// Handles backspace to remove characters from the buffer.
/// </summary>
public class InputAccumulator
{
    private readonly StringBuilder _buffer = new();
    private readonly Func<string, Task> _onInputComplete;
    private readonly object _lock = new();

    public InputAccumulator(Func<string, Task> onInputComplete)
    {
        _onInputComplete = onInputComplete ?? throw new ArgumentNullException(nameof(onInputComplete));
    }

    /// <summary>
    /// Appends input from the terminal. Call this from the userOutputHandler.
    /// </summary>
    public void Append(string input)
    {
        if (string.IsNullOrEmpty(input)) return;

        lock (_lock)
        {
            foreach (var c in input)
            {
                if (c == '\r' || c == '\n')
                {
                    // Enter pressed - fire callback with accumulated input
                    var text = _buffer.ToString().Trim();
                    _buffer.Clear();

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // Fire and forget - don't block the terminal
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _onInputComplete(text);
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"[VibeRails] Error in input callback: {ex.Message}");
                            }
                        });
                    }
                }
                else if (c == '\x7F' || c == '\b')
                {
                    // Backspace - remove last character
                    if (_buffer.Length > 0)
                    {
                        _buffer.Length--;
                    }
                }
                else if (c >= 32) // Printable characters only
                {
                    _buffer.Append(c);
                }
                // Ignore other control characters (arrows, etc.)
            }
        }
    }

    /// <summary>
    /// Gets the current buffer contents without clearing.
    /// </summary>
    public string CurrentBuffer
    {
        get
        {
            lock (_lock)
            {
                return _buffer.ToString();
            }
        }
    }

    /// <summary>
    /// Clears the buffer without firing the callback.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
        }
    }
}

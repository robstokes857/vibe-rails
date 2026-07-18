namespace PyBridge;

/// <summary>Which stream of the Python process a line of output came from.</summary>
public enum PythonOutputSource
{
    /// <summary>The line was written to <c>stdout</c>.</summary>
    StandardOutput,

    /// <summary>The line was written to <c>stderr</c>.</summary>
    StandardError,
}

/// <summary>
/// One line of live output from a running Python process, tagged with the stream it came
/// from and when it arrived (measured from process launch). This is what
/// <c>StreamAsync</c> / <see cref="PythonRun.Lines"/> yield, so a consumer can do:
/// <code>
/// await foreach (var line in runner.StreamFileAsync("train.py"))
///     Console.WriteLine($"[{line.Elapsed.TotalSeconds,6:F2}s] {line}");
/// </code>
/// </summary>
/// <param name="Source">Whether the line came from stdout or stderr.</param>
/// <param name="Text">The line's text, without the trailing newline.</param>
/// <param name="Elapsed">How long after process launch the line arrived.</param>
public readonly record struct PythonOutputLine(PythonOutputSource Source, string Text, TimeSpan Elapsed)
{
    /// <summary>True when the line came from stderr.</summary>
    public bool IsError => Source == PythonOutputSource.StandardError;

    /// <summary>The line's text — so <c>Console.WriteLine(line)</c> just prints it.</summary>
    public override string ToString() => Text;

    /// <summary>Lines convert implicitly to their text, for frictionless display and comparison.</summary>
    public static implicit operator string(PythonOutputLine line) => line.Text;
}

using System.Collections.Concurrent;
using System.Text;
using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Accumulates recent PTY output per session in a small rolling buffer and
/// publishes a "session_waiting_for_user" event when one of two prompt shapes
/// appears:
///   * Claude Code: adjacent •/◦ bullet lines at the same indent.
///   * Codex:       the "enter to submit ... esc to cancel" footer of a
///                  numbered confirm menu.
/// Buffering is required because PTY writes arrive in small chunks and neither
/// pattern ever co-occurs in a single write.
/// </summary>
public sealed class WaitingForUserInputObserver : ITerminalIoObserver
{
    private const int BufferCapacity = 16384;
    private const int CandidatePairMaxDistance = 256;
    private const int SpinnerContextRadius = 24;
    private static readonly TimeSpan MatchCooldown = TimeSpan.FromSeconds(30);

    private readonly IAppEventBus _eventBus;
    private readonly ConcurrentDictionary<string, SessionBuffer> _buffers = new();

    public WaitingForUserInputObserver(IAppEventBus eventBus)
    {
        _eventBus = eventBus;
    }

    public ValueTask OnTerminalIoAsync(TerminalIoEvent ioEvent, CancellationToken cancellationToken = default)
    {
        if (ioEvent.Direction != TerminalIoDirection.Output)
            return ValueTask.CompletedTask;

        var buffer = _buffers.GetOrAdd(ioEvent.SessionId, static _ => new SessionBuffer());

        // Frame-aware: a full-screen erase or alt-screen toggle resets the
        // accumulated window so bullet-lines from the prior frame don't pair
        // up with ones from the next frame (the main Bug #2 cause).
        if (RawChunkHasFrameBoundary(ioEvent.Text))
            buffer.ResetWindow();

        if (buffer.AppendAndCheck(ioEvent.PlainText))
        {
            _eventBus.Publish(
                "session_waiting_for_user",
                new SessionWaitingForUserPayload(ioEvent.SessionId),
                AppJsonSerializerContext.Default.SessionWaitingForUserPayload);
        }
        return ValueTask.CompletedTask;
    }

    private static bool RawChunkHasFrameBoundary(string rawText)
    {
        if (string.IsNullOrEmpty(rawText))
            return false;

        foreach (var part in TerminalTextSanitizer.ToTextWithControl(rawText))
        {
            if (!part.IsControl) continue;
            // \e[2J (EraseInDisplay with arg 2/3) or \e[?1049h/1047h (alt-screen enter)
            // are the reliable "new frame starts here" signals.
            if (part.ControlType == TerminalControlType.EraseInDisplay
                && (part.Raw.Contains("[2J", StringComparison.Ordinal)
                    || part.Raw.Contains("[3J", StringComparison.Ordinal)))
                return true;
            if (part.ControlType == TerminalControlType.SetMode
                && (part.Raw.Contains("?1049h", StringComparison.Ordinal)
                    || part.Raw.Contains("?1047h", StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    public ValueTask OnSessionCompleteAsync(TerminalSessionCompleteEvent completeEvent, CancellationToken cancellationToken = default)
    {
        _buffers.TryRemove(completeEvent.SessionId, out _);
        return ValueTask.CompletedTask;
    }

    private sealed class SessionBuffer
    {
        private static readonly string[] WorkingFragments = BuildWorkingFragments();
        private readonly StringBuilder _window = new(BufferCapacity);
        private readonly Lock _lock = new();
        private DateTime _cooldownUntilUtc = DateTime.MinValue;

        private readonly record struct BulletLine(
            int StartIndex,
            int EndIndexExclusive,
            int LineNumber,
            int Indent,
            char Glyph);

        public bool AppendAndCheck(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (now < _cooldownUntilUtc)
                    return false;

                _window.Append(text);
                if (_window.Length > BufferCapacity)
                    _window.Remove(0, _window.Length - BufferCapacity);

                var snapshot = _window.ToString();
                if (ContainsWaitingPrompt(snapshot) || ContainsCodexConfirmMenu(snapshot))
                {
                    _cooldownUntilUtc = now + MatchCooldown;
                    _window.Clear();
                    return true;
                }
                return false;
            }
        }

        public void ResetWindow()
        {
            lock (_lock)
            {
                _window.Clear();
            }
        }

        // Codex (and any OpenAI-TUI-style) confirm menu always emits both a
        // "enter to submit" affordance and an "esc to cancel" affordance in the
        // same screen. When both appear close together in the buffer the user
        // is being asked to pick.
        private static bool ContainsCodexConfirmMenu(string snapshot)
        {
            var submitIdx = snapshot.IndexOf("enter to submit", StringComparison.OrdinalIgnoreCase);
            if (submitIdx < 0) return false;
            var cancelIdx = snapshot.IndexOf("esc to cancel", StringComparison.OrdinalIgnoreCase);
            if (cancelIdx < 0) return false;
            return Math.Abs(submitIdx - cancelIdx) <= CandidatePairMaxDistance;
        }

        private static bool ContainsWaitingPrompt(string snapshot)
        {
            if (!snapshot.Contains('•') || !snapshot.Contains('◦'))
                return false;

            var bulletLines = FindBulletLines(snapshot);
            for (var i = 0; i < bulletLines.Count; i++)
            {
                var first = bulletLines[i];
                for (var j = i + 1; j < bulletLines.Count; j++)
                {
                    var second = bulletLines[j];
                    if (second.StartIndex - first.StartIndex > CandidatePairMaxDistance)
                        continue;

                    if (first.Glyph == second.Glyph)
                        continue;

                    // Prompt menus render both options at the same indentation
                    // level. Nested assistant bullet lists usually do not.
                    if (first.Indent != second.Indent)
                        continue;

                    if (second.LineNumber - first.LineNumber > 1)
                        continue;

                    var start = Math.Min(first.StartIndex, second.StartIndex);
                    var end = Math.Max(first.EndIndexExclusive, second.EndIndexExclusive);
                    if (HasNearbyWorkingFragment(snapshot, start, end))
                        continue;

                    return true;
                }
            }

            return false;
        }

        private static List<BulletLine> FindBulletLines(string snapshot)
        {
            var lines = new List<BulletLine>();
            var lineNumber = 0;
            var lineStart = 0;

            while (lineStart < snapshot.Length)
            {
                var lineEnd = lineStart;
                while (lineEnd < snapshot.Length && snapshot[lineEnd] is not '\r' and not '\n')
                    lineEnd++;

                if (TryCreateBulletLine(snapshot, lineStart, lineEnd, lineNumber, out var line))
                    lines.Add(line);

                if (lineEnd >= snapshot.Length)
                    break;

                lineStart = lineEnd + 1;
                if (snapshot[lineEnd] == '\r' && lineStart < snapshot.Length && snapshot[lineStart] == '\n')
                    lineStart++;

                lineNumber++;
            }

            return lines;
        }

        private static bool TryCreateBulletLine(string snapshot, int lineStart, int lineEndExclusive, int lineNumber, out BulletLine line)
        {
            var index = lineStart;
            while (index < lineEndExclusive && char.IsWhiteSpace(snapshot[index]))
                index++;

            if (index >= lineEndExclusive)
            {
                line = default;
                return false;
            }

            var glyph = snapshot[index];
            if (glyph is not '•' and not '◦')
            {
                line = default;
                return false;
            }

            var textStart = index + 1;
            if (textStart >= lineEndExclusive || !char.IsWhiteSpace(snapshot[textStart]))
            {
                line = default;
                return false;
            }

            while (textStart < lineEndExclusive && char.IsWhiteSpace(snapshot[textStart]))
                textStart++;

            if (textStart >= lineEndExclusive)
            {
                line = default;
                return false;
            }

            line = new BulletLine(
                StartIndex: lineStart,
                EndIndexExclusive: lineEndExclusive,
                LineNumber: lineNumber,
                Indent: index - lineStart,
                Glyph: glyph);
            return true;
        }

        // Claude/Codex spinner redraws can emit bullet pairs plus fragments of
        // "working" across multiple PTY writes. We only suppress matches when a
        // working fragment is very close to the bullet pair.
        private static bool HasNearbyWorkingFragment(string snapshot, int start, int end)
        {
            var contextStart = Math.Max(0, start - SpinnerContextRadius);
            var contextEnd = Math.Min(snapshot.Length, end + SpinnerContextRadius + 1);
            var lettersOnly = ExtractLettersLowerInvariant(snapshot, contextStart, contextEnd);
            if (lettersOnly.Length == 0)
                return false;

            foreach (var fragment in WorkingFragments)
            {
                if (lettersOnly.Contains(fragment, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string ExtractLettersLowerInvariant(string value, int start, int endExclusive)
        {
            var sb = new StringBuilder(endExclusive - start);
            for (var i = start; i < endExclusive; i++)
            {
                var ch = value[i];
                if (char.IsLetter(ch))
                    sb.Append(char.ToLowerInvariant(ch));
            }

            return sb.ToString();
        }

        private static string[] BuildWorkingFragments()
        {
            const string keyword = "working";
            var fragments = new HashSet<string>(StringComparer.Ordinal);

            for (var start = 0; start <= 2; start++)
            {
                for (var length = 4; start + length <= keyword.Length; length++)
                    fragments.Add(keyword.Substring(start, length));
            }

            return [.. fragments.OrderByDescending(static fragment => fragment.Length)];
        }
    }
}

using System.Collections.Concurrent;
using System.Text;
using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Accumulates recent PTY output per session in a small rolling buffer and
/// publishes a "session_waiting_for_user" event when the prompt glyph pattern
/// appears. Buffering is required because PTY writes arrive in small chunks and
/// the glyphs rarely co-occur in a single write.
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
        if (buffer.AppendAndCheck(ioEvent.PlainText))
        {
            _eventBus.Publish(
                "session_waiting_for_user",
                new SessionWaitingForUserPayload(ioEvent.SessionId),
                AppJsonSerializerContext.Default.SessionWaitingForUserPayload);
        }
        return ValueTask.CompletedTask;
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
                if (ContainsWaitingPrompt(snapshot))
                {
                    _cooldownUntilUtc = now + MatchCooldown;
                    _window.Clear();
                    return true;
                }
                return false;
            }
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

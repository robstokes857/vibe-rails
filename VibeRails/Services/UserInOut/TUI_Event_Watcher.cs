using System.Text;
using Serilog;

namespace VibeRails.Services.UserInOut;

public static class TUI_Event_Watcher
{
    private const char EscapeChar = '\x1B';
    private const string DefaultSessionId = "__default__";

    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, WatchState> s_sessionStates = new(StringComparer.Ordinal);
    private static readonly Dictionary<long, Subscription> s_subscriptions = new();
    private static long s_nextSubscriptionId;

    public readonly record struct TuiEvent(
        string SessionId,
        TUI_Event_Watcher_Type EventType,
        string Sequence,
        string? Payload,
        EventModifiers Modifiers,
        int? NumericValue,
        DateTimeOffset TimestampUtc)
    {
        public bool HasPayload => !string.IsNullOrEmpty(Payload);
    }

    [Flags]
    public enum EventModifiers
    {
        None = 0,
        Shift = 1,
        Alt = 2,
        Ctrl = 4
    }

    public sealed class SubscriptionOptions
    {
        public IReadOnlyCollection<TUI_Event_Watcher_Type>? EventTypes { get; init; }
        public Predicate<TuiEvent>? Filter { get; init; }
    }

    public static IDisposable Subscribe(Action<TuiEvent> callback, params TUI_Event_Watcher_Type[] eventTypes)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return Subscribe(evt =>
        {
            callback(evt);
            return Task.CompletedTask;
        }, eventTypes);
    }

    public static IDisposable Subscribe(Func<TuiEvent, Task> callback, params TUI_Event_Watcher_Type[] eventTypes)
    {
        return Subscribe(callback, new SubscriptionOptions
        {
            EventTypes = eventTypes.Length == 0 ? null : eventTypes
        });
    }

    public static IDisposable Subscribe(Func<TuiEvent, Task> callback, SubscriptionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var subscription = new Subscription(
            Interlocked.Increment(ref s_nextSubscriptionId),
            callback,
            options?.EventTypes,
            options?.Filter);

        lock (s_lock)
        {
            s_subscriptions[subscription.Id] = subscription;
        }

        return new SubscriptionHandle(subscription.Id);
    }

    public static IReadOnlyList<TuiEvent> Watch(string input)
    {
        return Watch(DefaultSessionId, input);
    }

    public static IReadOnlyList<TuiEvent> Watch(string sessionId, string input)
    {
        if (string.IsNullOrEmpty(input))
            return Array.Empty<TuiEvent>();

        var normalizedSessionId = NormalizeSessionId(sessionId);
        var events = new List<TuiEvent>();
        Subscription[] subscriptions;

        lock (s_lock)
        {
            var state = GetOrCreateState(normalizedSessionId);
            for (var i = 0; i < input.Length; i++)
            {
                ProcessChar(normalizedSessionId, state, input[i], i == input.Length - 1, events);
            }

            if (state.IsIdle)
            {
                s_sessionStates.Remove(normalizedSessionId);
            }

            subscriptions = [.. s_subscriptions.Values];
        }

        Dispatch(events, subscriptions);
        return events.Count == 0 ? Array.Empty<TuiEvent>() : events;
    }

    public static void ClearSession(string sessionId)
    {
        lock (s_lock)
        {
            s_sessionStates.Remove(NormalizeSessionId(sessionId));
        }
    }

    private static string NormalizeSessionId(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? DefaultSessionId : sessionId;

    private static WatchState GetOrCreateState(string sessionId)
    {
        if (!s_sessionStates.TryGetValue(sessionId, out var state))
        {
            state = new WatchState();
            s_sessionStates[sessionId] = state;
        }

        return state;
    }

    private static void ProcessChar(string sessionId, WatchState state, char c, bool isLastCharInChunk, List<TuiEvent> events)
    {
        if (state.InBracketedPaste)
        {
            ProcessCharInsidePaste(sessionId, state, c, events);
            return;
        }

        ProcessCharOutsidePaste(sessionId, state, c, isLastCharInChunk, events);
    }

    private static void ProcessCharOutsidePaste(string sessionId, WatchState state, char c, bool isLastCharInChunk, List<TuiEvent> events)
    {
        while (true)
        {
            if (state.IgnoreNextLineFeed)
            {
                state.IgnoreNextLineFeed = false;
                if (c == '\n')
                    return;
            }

            if (state.EscapeState != EscapeParseState.None)
            {
                var result = ConsumeEscapeOutsidePaste(sessionId, state, c, events);
                if (result == EscapeConsumeResult.ReprocessCurrentChar)
                    continue;
                if (result == EscapeConsumeResult.Consumed)
                    return;
            }

            switch (c)
            {
                case EscapeChar:
                    if (isLastCharInChunk)
                    {
                        events.Add(CreateEvent(sessionId, TUI_Event_Watcher_Type.Escape, "\x1B"));
                    }
                    else
                    {
                        BeginEscapeSequence(state);
                    }
                    return;

                case '\r':
                    events.Add(CreateEvent(sessionId, TUI_Event_Watcher_Type.Enter, "\r"));
                    state.IgnoreNextLineFeed = true;
                    return;

                case '\n':
                    events.Add(CreateEvent(sessionId, TUI_Event_Watcher_Type.Enter, "\n"));
                    return;

                case '\t':
                    events.Add(CreateEvent(sessionId, TUI_Event_Watcher_Type.Tab, "\t"));
                    return;

                case '\b':
                    events.Add(CreateEvent(sessionId, TUI_Event_Watcher_Type.Backspace, "\b"));
                    return;

                case '\x7F':
                    events.Add(CreateEvent(sessionId, TUI_Event_Watcher_Type.Backspace, "\x7F"));
                    return;

                case '\x03':
                    events.Add(CreateEvent(sessionId, TUI_Event_Watcher_Type.CtrlC, "\x03", modifiers: EventModifiers.Ctrl));
                    return;

                case '\x04':
                    events.Add(CreateEvent(sessionId, TUI_Event_Watcher_Type.CtrlD, "\x04", modifiers: EventModifiers.Ctrl));
                    return;

                case '\x0C':
                    events.Add(CreateEvent(sessionId, TUI_Event_Watcher_Type.CtrlL, "\x0C", modifiers: EventModifiers.Ctrl));
                    return;

                case '\x1A':
                    events.Add(CreateEvent(sessionId, TUI_Event_Watcher_Type.CtrlZ, "\x1A", modifiers: EventModifiers.Ctrl));
                    return;

                default:
                    return;
            }
        }
    }

    private static void ProcessCharInsidePaste(string sessionId, WatchState state, char c, List<TuiEvent> events)
    {
        while (true)
        {
            if (state.IgnoreNextPasteLineFeed)
            {
                state.IgnoreNextPasteLineFeed = false;
                if (c == '\n')
                    return;
            }

            switch (state.EscapeState)
            {
                case EscapeParseState.None:
                    if (c == EscapeChar)
                    {
                        BeginEscapeSequence(state);
                        return;
                    }

                    AppendPasteChar(state, c);
                    return;

                case EscapeParseState.AfterEsc:
                    if (c == EscapeChar)
                    {
                        state.PasteBuffer.Append(EscapeChar);
                        BeginEscapeSequence(state);
                        return;
                    }

                    if (c == '[')
                    {
                        state.SequenceBuffer.Append(c);
                        state.CsiParameters.Clear();
                        state.EscapeState = EscapeParseState.Csi;
                        return;
                    }

                    state.PasteBuffer.Append(EscapeChar);
                    ResetEscapeSequence(state);
                    continue;

                case EscapeParseState.Csi:
                    state.SequenceBuffer.Append(c);
                    if (IsEscapeFinalByte(c))
                    {
                        var parameters = state.CsiParameters.ToString();
                        if (c == '~' && parameters == "201")
                        {
                            var payload = state.PasteBuffer.ToString();
                            state.PasteBuffer.Clear();
                            state.InBracketedPaste = false;
                            ResetEscapeSequence(state);

                            events.Add(CreateEvent(
                                sessionId,
                                TUI_Event_Watcher_Type.BracketedPaste,
                                "\x1B[201~",
                                payload: payload));

                            if (payload == "\n")
                            {
                                events.Add(CreateEvent(
                                    sessionId,
                                    TUI_Event_Watcher_Type.ShiftEnter,
                                    "\x1B[201~",
                                    payload: payload,
                                    modifiers: EventModifiers.Shift));
                            }

                            return;
                        }

                        state.PasteBuffer.Append(state.SequenceBuffer.ToString());
                        ResetEscapeSequence(state);
                        return;
                    }

                    state.CsiParameters.Append(c);
                    return;

                default:
                    state.PasteBuffer.Append(state.SequenceBuffer.ToString());
                    ResetEscapeSequence(state);
                    continue;
            }
        }
    }

    private static EscapeConsumeResult ConsumeEscapeOutsidePaste(string sessionId, WatchState state, char c, List<TuiEvent> events)
    {
        switch (state.EscapeState)
        {
            case EscapeParseState.AfterEsc:
                if (c == EscapeChar)
                {
                    events.Add(CreateEvent(sessionId, TUI_Event_Watcher_Type.Escape, "\x1B"));
                    BeginEscapeSequence(state);
                    return EscapeConsumeResult.Consumed;
                }

                switch (c)
                {
                    case '[':
                        state.SequenceBuffer.Append(c);
                        state.CsiParameters.Clear();
                        state.EscapeState = EscapeParseState.Csi;
                        return EscapeConsumeResult.Consumed;

                    case 'O':
                        state.SequenceBuffer.Append(c);
                        state.EscapeState = EscapeParseState.Ss3;
                        return EscapeConsumeResult.Consumed;

                    case ']':
                        state.SequenceBuffer.Append(c);
                        state.EscapeStringSawEsc = false;
                        state.EscapeState = EscapeParseState.Osc;
                        return EscapeConsumeResult.Consumed;

                    case 'P':
                    case '^':
                    case '_':
                        state.SequenceBuffer.Append(c);
                        state.EscapeStringSawEsc = false;
                        state.EscapeState = EscapeParseState.EscString;
                        return EscapeConsumeResult.Consumed;

                    default:
                        ResetEscapeSequence(state);
                        events.Add(CreateEvent(sessionId, TUI_Event_Watcher_Type.Escape, "\x1B"));
                        return EscapeConsumeResult.ReprocessCurrentChar;
                }

            case EscapeParseState.Csi:
                state.SequenceBuffer.Append(c);
                if (IsEscapeFinalByte(c))
                {
                    var parameters = state.CsiParameters.ToString();
                    var sequence = state.SequenceBuffer.ToString();
                    if (TryTranslateCsiEvent(sessionId, state, parameters, c, sequence, out var parsedEvent))
                    {
                        events.Add(parsedEvent);
                    }

                    ResetEscapeSequence(state);
                }
                else
                {
                    state.CsiParameters.Append(c);
                }
                return EscapeConsumeResult.Consumed;

            case EscapeParseState.Ss3:
                state.SequenceBuffer.Append(c);
                if (IsEscapeFinalByte(c))
                {
                    var sequence = state.SequenceBuffer.ToString();
                    if (TryTranslateSs3Event(sessionId, c, sequence, out var parsedEvent))
                    {
                        events.Add(parsedEvent);
                    }

                    ResetEscapeSequence(state);
                }
                return EscapeConsumeResult.Consumed;

            case EscapeParseState.Osc:
            case EscapeParseState.EscString:
                if (c == '\a')
                {
                    ResetEscapeSequence(state);
                    return EscapeConsumeResult.Consumed;
                }

                if (state.EscapeStringSawEsc && c == '\\')
                {
                    ResetEscapeSequence(state);
                    return EscapeConsumeResult.Consumed;
                }

                state.EscapeStringSawEsc = c == EscapeChar;
                return EscapeConsumeResult.Consumed;

            default:
                ResetEscapeSequence(state);
                return EscapeConsumeResult.ReprocessCurrentChar;
        }
    }

    private static bool TryTranslateCsiEvent(
        string sessionId,
        WatchState state,
        string parameters,
        char finalByte,
        string sequence,
        out TuiEvent parsedEvent)
    {
        parsedEvent = default;
        switch (finalByte)
        {
            case 'A':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.UpArrow, sequence, modifiers: ParseTrailingModifier(parameters));
                return true;

            case 'B':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.DownArrow, sequence, modifiers: ParseTrailingModifier(parameters));
                return true;

            case 'C':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.RightArrow, sequence, modifiers: ParseTrailingModifier(parameters));
                return true;

            case 'D':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.LeftArrow, sequence, modifiers: ParseTrailingModifier(parameters));
                return true;

            case 'H':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.Home, sequence, modifiers: ParseTrailingModifier(parameters));
                return true;

            case 'F':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.End, sequence, modifiers: ParseTrailingModifier(parameters));
                return true;

            case 'I':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.TerminalFocus, sequence);
                return true;

            case 'O':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.TerminalLostFocus, sequence);
                return true;

            case 'Z':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.ShiftTab, sequence, modifiers: EventModifiers.Shift);
                return true;

            case '~':
                var keyCode = ParseFirstParameter(parameters);
                var modifiers = ParseTrailingModifier(parameters);
                if (!keyCode.HasValue)
                    return false;

                switch (keyCode.Value)
                {
                    case 1:
                    case 7:
                        parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.Home, sequence, modifiers: modifiers);
                        return true;

                    case 2:
                        parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.Insert, sequence, modifiers: modifiers);
                        return true;

                    case 3:
                        parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.Delete, sequence, modifiers: modifiers);
                        return true;

                    case 4:
                    case 8:
                        parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.End, sequence, modifiers: modifiers);
                        return true;

                    case 5:
                        parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.PageUp, sequence, modifiers: modifiers);
                        return true;

                    case 6:
                        parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.PageDown, sequence, modifiers: modifiers);
                        return true;

                    case 11:
                        parsedEvent = CreateFunctionKeyEvent(sessionId, 1, sequence, modifiers);
                        return true;

                    case 12:
                        parsedEvent = CreateFunctionKeyEvent(sessionId, 2, sequence, modifiers);
                        return true;

                    case 13:
                        parsedEvent = CreateFunctionKeyEvent(sessionId, 3, sequence, modifiers);
                        return true;

                    case 14:
                        parsedEvent = CreateFunctionKeyEvent(sessionId, 4, sequence, modifiers);
                        return true;

                    case 15:
                        parsedEvent = CreateFunctionKeyEvent(sessionId, 5, sequence, modifiers);
                        return true;

                    case 17:
                        parsedEvent = CreateFunctionKeyEvent(sessionId, 6, sequence, modifiers);
                        return true;

                    case 18:
                        parsedEvent = CreateFunctionKeyEvent(sessionId, 7, sequence, modifiers);
                        return true;

                    case 19:
                        parsedEvent = CreateFunctionKeyEvent(sessionId, 8, sequence, modifiers);
                        return true;

                    case 20:
                        parsedEvent = CreateFunctionKeyEvent(sessionId, 9, sequence, modifiers);
                        return true;

                    case 21:
                        parsedEvent = CreateFunctionKeyEvent(sessionId, 10, sequence, modifiers);
                        return true;

                    case 23:
                        parsedEvent = CreateFunctionKeyEvent(sessionId, 11, sequence, modifiers);
                        return true;

                    case 24:
                        parsedEvent = CreateFunctionKeyEvent(sessionId, 12, sequence, modifiers);
                        return true;

                    case 200:
                        state.InBracketedPaste = true;
                        state.PasteBuffer.Clear();
                        state.IgnoreNextPasteLineFeed = false;
                        return false;

                    case 201:
                        return false;

                    default:
                        return false;
                }

            default:
                return false;
        }
    }

    private static bool TryTranslateSs3Event(string sessionId, char finalByte, string sequence, out TuiEvent parsedEvent)
    {
        parsedEvent = default;
        switch (finalByte)
        {
            case 'A':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.UpArrow, sequence);
                return true;

            case 'B':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.DownArrow, sequence);
                return true;

            case 'C':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.RightArrow, sequence);
                return true;

            case 'D':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.LeftArrow, sequence);
                return true;

            case 'F':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.End, sequence);
                return true;

            case 'H':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.Home, sequence);
                return true;

            case 'M':
                parsedEvent = CreateEvent(sessionId, TUI_Event_Watcher_Type.Enter, sequence);
                return true;

            case 'P':
                parsedEvent = CreateFunctionKeyEvent(sessionId, 1, sequence, EventModifiers.None);
                return true;

            case 'Q':
                parsedEvent = CreateFunctionKeyEvent(sessionId, 2, sequence, EventModifiers.None);
                return true;

            case 'R':
                parsedEvent = CreateFunctionKeyEvent(sessionId, 3, sequence, EventModifiers.None);
                return true;

            case 'S':
                parsedEvent = CreateFunctionKeyEvent(sessionId, 4, sequence, EventModifiers.None);
                return true;

            default:
                return false;
        }
    }

    private static void BeginEscapeSequence(WatchState state)
    {
        state.SequenceBuffer.Clear();
        state.CsiParameters.Clear();
        state.SequenceBuffer.Append(EscapeChar);
        state.EscapeStringSawEsc = false;
        state.EscapeState = EscapeParseState.AfterEsc;
    }

    private static void ResetEscapeSequence(WatchState state)
    {
        state.SequenceBuffer.Clear();
        state.CsiParameters.Clear();
        state.EscapeStringSawEsc = false;
        state.EscapeState = EscapeParseState.None;
    }

    private static void AppendPasteChar(WatchState state, char c)
    {
        if (c == '\r')
        {
            state.PasteBuffer.Append('\n');
            state.IgnoreNextPasteLineFeed = true;
            return;
        }

        state.PasteBuffer.Append(c);
    }

    private static bool IsEscapeFinalByte(char c) => c is >= '@' and <= '~';

    private static int? ParseFirstParameter(string parameters)
    {
        if (string.IsNullOrEmpty(parameters))
            return null;

        var separatorIndex = parameters.IndexOf(';');
        var firstSegment = separatorIndex >= 0 ? parameters[..separatorIndex] : parameters;
        return int.TryParse(firstSegment, out var value) ? value : null;
    }

    private static EventModifiers ParseTrailingModifier(string parameters)
    {
        if (string.IsNullOrEmpty(parameters))
            return EventModifiers.None;

        var segments = parameters.Split(';');
        if (segments.Length < 2)
            return EventModifiers.None;

        return int.TryParse(segments[^1], out var modifierValue)
            ? modifierValue switch
            {
                2 => EventModifiers.Shift,
                3 => EventModifiers.Alt,
                4 => EventModifiers.Shift | EventModifiers.Alt,
                5 => EventModifiers.Ctrl,
                6 => EventModifiers.Shift | EventModifiers.Ctrl,
                7 => EventModifiers.Alt | EventModifiers.Ctrl,
                8 => EventModifiers.Shift | EventModifiers.Alt | EventModifiers.Ctrl,
                _ => EventModifiers.None
            }
            : EventModifiers.None;
    }

    private static TuiEvent CreateFunctionKeyEvent(
        string sessionId,
        int number,
        string sequence,
        EventModifiers modifiers)
    {
        return CreateEvent(
            sessionId,
            TUI_Event_Watcher_Type.FunctionKey,
            sequence,
            modifiers: modifiers,
            numericValue: number);
    }

    private static TuiEvent CreateEvent(
        string sessionId,
        TUI_Event_Watcher_Type eventType,
        string sequence,
        string? payload = null,
        EventModifiers modifiers = EventModifiers.None,
        int? numericValue = null)
    {
        return new TuiEvent(
            sessionId,
            eventType,
            sequence,
            payload,
            modifiers,
            numericValue,
            DateTimeOffset.UtcNow);
    }

    private static void Dispatch(IReadOnlyList<TuiEvent> events, IReadOnlyList<Subscription> subscriptions)
    {
        if (events.Count == 0 || subscriptions.Count == 0)
            return;

        foreach (var tuiEvent in events)
        {
            foreach (var subscription in subscriptions)
            {
                if (!subscription.Matches(tuiEvent))
                    continue;

                try
                {
                    var task = subscription.Callback(tuiEvent);
                    if (!task.IsCompletedSuccessfully)
                    {
                        _ = ObserveCallbackAsync(task, tuiEvent);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[TUI_Event_Watcher] Subscriber failed for {EventType}", tuiEvent.EventType);
                }
            }
        }
    }

    private static async Task ObserveCallbackAsync(Task callbackTask, TuiEvent tuiEvent)
    {
        try
        {
            await callbackTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[TUI_Event_Watcher] Async subscriber failed for {EventType}", tuiEvent.EventType);
        }
    }

    private static void RemoveSubscription(long id)
    {
        lock (s_lock)
        {
            s_subscriptions.Remove(id);
        }
    }

    private sealed class Subscription
    {
        public Subscription(
            long id,
            Func<TuiEvent, Task> callback,
            IReadOnlyCollection<TUI_Event_Watcher_Type>? eventTypes,
            Predicate<TuiEvent>? filter)
        {
            Id = id;
            Callback = callback;
            EventTypes = eventTypes?.Count > 0 ? new HashSet<TUI_Event_Watcher_Type>(eventTypes) : null;
            Filter = filter;
        }

        public long Id { get; }
        public Func<TuiEvent, Task> Callback { get; }
        public HashSet<TUI_Event_Watcher_Type>? EventTypes { get; }
        public Predicate<TuiEvent>? Filter { get; }

        public bool Matches(TuiEvent tuiEvent)
        {
            if (EventTypes != null && !EventTypes.Contains(tuiEvent.EventType))
                return false;

            return Filter?.Invoke(tuiEvent) ?? true;
        }
    }

    private sealed class SubscriptionHandle : IDisposable
    {
        private long _subscriptionId;

        public SubscriptionHandle(long subscriptionId)
        {
            _subscriptionId = subscriptionId;
        }

        public void Dispose()
        {
            var subscriptionId = Interlocked.Exchange(ref _subscriptionId, 0);
            if (subscriptionId == 0)
                return;

            RemoveSubscription(subscriptionId);
        }
    }

    private sealed class WatchState
    {
        public EscapeParseState EscapeState { get; set; }
        public bool EscapeStringSawEsc { get; set; }
        public bool IgnoreNextLineFeed { get; set; }
        public bool InBracketedPaste { get; set; }
        public bool IgnoreNextPasteLineFeed { get; set; }
        public StringBuilder SequenceBuffer { get; } = new();
        public StringBuilder CsiParameters { get; } = new();
        public StringBuilder PasteBuffer { get; } = new();

        public bool IsIdle =>
            !InBracketedPaste &&
            !IgnoreNextLineFeed &&
            !IgnoreNextPasteLineFeed &&
            EscapeState == EscapeParseState.None;
    }

    private enum EscapeParseState
    {
        None = 0,
        AfterEsc = 1,
        Csi = 2,
        Ss3 = 3,
        Osc = 4,
        EscString = 5
    }

    private enum EscapeConsumeResult
    {
        Consumed = 0,
        ReprocessCurrentChar = 1
    }
}

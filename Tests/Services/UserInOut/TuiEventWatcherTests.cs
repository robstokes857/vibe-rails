using VibeRails.Services.UserInOut;
using Xunit;

namespace Tests.Services.UserInOut;

public class TuiEventWatcherTests
{
    [Fact]
    public void Watch_DetectsCommonSingleKeyEvents()
    {
        var sessionId = Guid.NewGuid().ToString();

        var escapeEvents = TUI_Event_Watcher.Watch(sessionId, "\x1B");
        var enterEvents = TUI_Event_Watcher.Watch(sessionId, "\r\n");
        var tabEvents = TUI_Event_Watcher.Watch(sessionId, "\t");
        var backspaceEvents = TUI_Event_Watcher.Watch(sessionId, "\x7F");
        var ctrlCEvents = TUI_Event_Watcher.Watch(sessionId, "\x03");

        Assert.Single(escapeEvents);
        Assert.Equal(TUI_Event_Watcher_Type.Escape, escapeEvents[0].EventType);

        Assert.Single(enterEvents);
        Assert.Equal(TUI_Event_Watcher_Type.Enter, enterEvents[0].EventType);

        Assert.Single(tabEvents);
        Assert.Equal(TUI_Event_Watcher_Type.Tab, tabEvents[0].EventType);

        Assert.Single(backspaceEvents);
        Assert.Equal(TUI_Event_Watcher_Type.Backspace, backspaceEvents[0].EventType);

        Assert.Single(ctrlCEvents);
        Assert.Equal(TUI_Event_Watcher_Type.CtrlC, ctrlCEvents[0].EventType);
        Assert.Equal(TUI_Event_Watcher.EventModifiers.Ctrl, ctrlCEvents[0].Modifiers);
    }

    [Fact]
    public void Watch_DetectsNavigationAndFocusSequences()
    {
        var sessionId = Guid.NewGuid().ToString();

        var events = TUI_Event_Watcher.Watch(
            sessionId,
            "\x1B[A\x1B[3~\x1B[Z\x1B[I\x1B[O\x1BOP");

        Assert.Equal(6, events.Count);
        Assert.Equal(TUI_Event_Watcher_Type.UpArrow, events[0].EventType);
        Assert.Equal(TUI_Event_Watcher_Type.Delete, events[1].EventType);
        Assert.Equal(TUI_Event_Watcher_Type.ShiftTab, events[2].EventType);
        Assert.Equal(TUI_Event_Watcher_Type.TerminalFocus, events[3].EventType);
        Assert.Equal(TUI_Event_Watcher_Type.TerminalLostFocus, events[4].EventType);
        Assert.Equal(TUI_Event_Watcher_Type.FunctionKey, events[5].EventType);
        Assert.Equal(1, events[5].NumericValue);
    }

    [Fact]
    public void Watch_DetectsBracketedPasteAndShiftEnter()
    {
        var sessionId = Guid.NewGuid().ToString();

        var events = TUI_Event_Watcher.Watch(sessionId, "\x1B[200~\n\x1B[201~");

        Assert.Equal(2, events.Count);
        Assert.Equal(TUI_Event_Watcher_Type.BracketedPaste, events[0].EventType);
        Assert.Equal("\n", events[0].Payload);
        Assert.Equal(TUI_Event_Watcher_Type.ShiftEnter, events[1].EventType);
        Assert.Equal(TUI_Event_Watcher.EventModifiers.Shift, events[1].Modifiers);
    }

    [Fact]
    public void Watch_KeepsPartialSequencesScopedPerSession()
    {
        var sessionA = Guid.NewGuid().ToString();
        var sessionB = Guid.NewGuid().ToString();

        var firstChunk = TUI_Event_Watcher.Watch(sessionA, "\x1B[");
        var wrongSession = TUI_Event_Watcher.Watch(sessionB, "A");
        var completed = TUI_Event_Watcher.Watch(sessionA, "A");

        Assert.Empty(firstChunk);
        Assert.Empty(wrongSession);
        Assert.Single(completed);
        Assert.Equal(TUI_Event_Watcher_Type.UpArrow, completed[0].EventType);
        Assert.Equal(sessionA, completed[0].SessionId);
    }

    [Fact]
    public void Subscribe_CanFilterToSubset_AndUnsubscribe()
    {
        var sessionId = Guid.NewGuid().ToString();
        var captured = new List<TUI_Event_Watcher.TuiEvent>();

        using var subscription = TUI_Event_Watcher.Subscribe(
            evt =>
            {
                captured.Add(evt);
                return Task.CompletedTask;
            },
            TUI_Event_Watcher_Type.Escape,
            TUI_Event_Watcher_Type.Enter);

        TUI_Event_Watcher.Watch(sessionId, "\x1B[A");
        TUI_Event_Watcher.Watch(sessionId, "\x1B");
        TUI_Event_Watcher.Watch(sessionId, "\n");

        subscription.Dispose();
        TUI_Event_Watcher.Watch(sessionId, "\x1B");

        Assert.Equal(2, captured.Count);
        Assert.Equal(TUI_Event_Watcher_Type.Escape, captured[0].EventType);
        Assert.Equal(TUI_Event_Watcher_Type.Enter, captured[1].EventType);
    }
}

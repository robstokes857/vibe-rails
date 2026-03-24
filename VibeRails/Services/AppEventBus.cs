using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services;

public sealed class AppEventBus : IAppEventBus
{
    private readonly Lock _lock = new();
    private readonly List<Action<AppEvent>> _listeners = [];

    public void Publish<T>(string type, T payload, JsonTypeInfo<T> typeInfo)
    {
        var element = JsonSerializer.SerializeToElement(payload, typeInfo);
        var appEvent = new AppEvent(type, element);

        Action<AppEvent>[] listeners;
        lock (_lock)
        {
            listeners = [.. _listeners];
        }

        foreach (var listener in listeners)
        {
            listener(appEvent);
        }
    }

    public IDisposable Subscribe(Action<AppEvent> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        lock (_lock)
        {
            _listeners.Add(listener);
        }

        return new Subscription(this, listener);
    }

    private void Unsubscribe(Action<AppEvent> listener)
    {
        lock (_lock)
        {
            _listeners.Remove(listener);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly AppEventBus _owner;
        private Action<AppEvent>? _listener;

        public Subscription(AppEventBus owner, Action<AppEvent> listener)
        {
            _owner = owner;
            _listener = listener;
        }

        public void Dispose()
        {
            if (_listener == null)
                return;

            _owner.Unsubscribe(_listener);
            _listener = null;
        }
    }
}

using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services
{
    public sealed class AppNotificationService : IAppNotificationService
    {
        private readonly Lock _lock = new();
        private readonly List<Action<AppToastNotification>> _toastListeners = [];

        public ValueTask PublishToastAsync(
            AppToastNotification notification,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();           
            Action<AppToastNotification>[] listeners;
            lock (_lock)
            {
                listeners = [.. _toastListeners];
            }

            foreach (var listener in listeners)
            {               
                    listener(notification);               
            }
            return ValueTask.CompletedTask;
        }

        public IDisposable SubscribeToToasts(Action<AppToastNotification> listener)
        {
            ArgumentNullException.ThrowIfNull(listener);

            lock (_lock)
            {
                _toastListeners.Add(listener);
            }

            return new Subscription(this, listener);
        }

        private void Unsubscribe(Action<AppToastNotification> listener)
        {
            lock (_lock)
            {
                _toastListeners.Remove(listener);
            }
        }

        private sealed class Subscription : IDisposable
        {
            private readonly AppNotificationService _owner;
            private Action<AppToastNotification>? _listener;

            public Subscription(AppNotificationService owner, Action<AppToastNotification> listener)
            {
                _owner = owner;
                _listener = listener;
            }

            public void Dispose()
            {
                if (_listener == null)
                {
                    return;
                }

                _owner.Unsubscribe(_listener);
                _listener = null;
            }
        }
    }
}

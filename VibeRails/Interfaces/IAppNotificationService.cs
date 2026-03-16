using VibeRails.DTOs;

namespace VibeRails.Interfaces
{
    public interface IAppNotificationService
    {
        ValueTask PublishToastAsync(
            AppToastNotification notification,
            CancellationToken cancellationToken = default);

        IDisposable SubscribeToToasts(Action<AppToastNotification> listener);
    }
}

using System.Text.Json.Serialization.Metadata;
using VibeRails.DTOs;

namespace VibeRails.Interfaces;

public interface IAppEventBus
{
    void Publish<T>(string type, T payload, JsonTypeInfo<T> typeInfo);
    IDisposable Subscribe(Action<AppEvent> listener);
}

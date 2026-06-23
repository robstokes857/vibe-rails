using System.Collections.Concurrent;
using VibeRails.DB;

namespace VibeRails.Services
{
    /// <summary>
    /// Project-agnostic key-value cache backed by the GlobalCache table. Mirrors
    /// <see cref="ProjectCache"/> but drops the per-project path dimension, so a value
    /// written here is visible regardless of which project is active.
    /// </summary>
    public class GlobalCache : IGlobalCache
    {
        private readonly IRepository _repository;
        private readonly ConcurrentDictionary<string, string?> _cache = new();

        public GlobalCache(IRepository repository)
        {
            _repository = repository;
        }

        public async Task<string?> GetAsync(string key)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            var value = await _repository.GetGlobalCacheValueAsync(key);
            _cache[key] = value;
            return value;
        }

        public async Task SetAsync(string key, string value)
        {
            await _repository.SetGlobalCacheValueAsync(key, value);
            _cache[key] = value;
        }

        public async Task<bool> GetAsBoolAsync(string key, bool defaultValue = false)
        {
            var value = await GetAsync(key);
            if (value == null) return defaultValue;
            return bool.TryParse(value, out var result) ? result : defaultValue;
        }

        public async Task<int?> GetAsIntAsync(string key)
        {
            var value = await GetAsync(key);
            if (value == null) return null;
            return int.TryParse(value, out var result) ? result : null;
        }

        public async Task<Dictionary<string, string>> GetAllAsync()
        {
            var all = await _repository.GetAllGlobalCacheAsync();

            foreach (var kvp in all)
                _cache[kvp.Key] = kvp.Value;

            return all;
        }

        public async Task RemoveAsync(string key)
        {
            await _repository.RemoveGlobalCacheValueAsync(key);
            _cache.TryRemove(key, out _);
        }
    }
}

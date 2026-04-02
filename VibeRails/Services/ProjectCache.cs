using System.Collections.Concurrent;
using VibeRails.DB;
using VibeRails.Utils;

namespace VibeRails.Services
{
    public class ProjectCache : IProjectCache
    {
        private readonly IRepository _repository;
        private readonly string _defaultProjectPath;
        private readonly ConcurrentDictionary<string, string?> _cache = new();

        public ProjectCache(IRepository repository)
        {
            _repository = repository;
            _defaultProjectPath = ParserConfigs.GetRootPath();
            if (string.IsNullOrEmpty(_defaultProjectPath))
            {
                _defaultProjectPath = Directory.GetCurrentDirectory();
            }
        }

        private static string CacheKey(string projectPath, string key) => $"{projectPath}::{key}";

        public async Task<string?> GetAsync(string key, string? projectPath = null)
        {
            var path = projectPath ?? _defaultProjectPath;
            var ck = CacheKey(path, key);

            if (_cache.TryGetValue(ck, out var cached))
                return cached;

            var value = await _repository.GetProjectCacheValueAsync(path, key);
            _cache[ck] = value;
            return value;
        }

        public async Task SetAsync(string key, string value, string? projectPath = null)
        {
            var path = projectPath ?? _defaultProjectPath;
            await _repository.SetProjectCacheValueAsync(path, key, value);
            _cache[CacheKey(path, key)] = value;
        }

        public async Task<bool> GetAsBoolAsync(string key, bool defaultValue = false, string? projectPath = null)
        {
            var value = await GetAsync(key, projectPath);
            if (value == null) return defaultValue;
            return bool.TryParse(value, out var result) ? result : defaultValue;
        }

        public async Task<int?> GetAsIntAsync(string key, string? projectPath = null)
        {
            var value = await GetAsync(key, projectPath);
            if (value == null) return null;
            return int.TryParse(value, out var result) ? result : null;
        }

        public async Task<Dictionary<string, string>> GetAllAsync(string? projectPath = null)
        {
            var path = projectPath ?? _defaultProjectPath;
            var all = await _repository.GetAllProjectCacheAsync(path);

            foreach (var kvp in all)
                _cache[CacheKey(path, kvp.Key)] = kvp.Value;

            return all;
        }

        public async Task RemoveAsync(string key, string? projectPath = null)
        {
            var path = projectPath ?? _defaultProjectPath;
            await _repository.RemoveProjectCacheValueAsync(path, key);
            _cache.TryRemove(CacheKey(path, key), out _);
        }
    }
}

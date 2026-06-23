namespace VibeRails.Services
{
    /// <summary>
    /// Generic key-value store that is NOT scoped to a project (unlike <see cref="IProjectCache"/>).
    /// Use it for machine/user-wide flags that must outlive and span projects.
    /// </summary>
    public interface IGlobalCache
    {
        Task<string?> GetAsync(string key);
        Task SetAsync(string key, string value);
        Task<bool> GetAsBoolAsync(string key, bool defaultValue = false);
        Task<int?> GetAsIntAsync(string key);
        Task<Dictionary<string, string>> GetAllAsync();
        Task RemoveAsync(string key);
    }
}

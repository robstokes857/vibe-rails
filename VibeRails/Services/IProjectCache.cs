namespace VibeRails.Services
{
    public interface IProjectCache
    {
        Task<string?> GetAsync(string key, string? projectPath = null);
        Task SetAsync(string key, string value, string? projectPath = null);
        Task<bool> GetAsBoolAsync(string key, bool defaultValue = false, string? projectPath = null);
        Task<int?> GetAsIntAsync(string key, string? projectPath = null);
        Task<Dictionary<string, string>> GetAllAsync(string? projectPath = null);
        Task RemoveAsync(string key, string? projectPath = null);
    }
}

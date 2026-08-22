using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace DshDesktop.Services;

/// <summary>持久化的单个插件条目（多来源合并后的展示/缓存模型）。</summary>
public sealed class PluginCacheEntry
{
    public string PackageName { get; set; } = "";
    public string? RepoFullName { get; set; }
    public string? RepoUrl { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public int Stars { get; set; }
    public int Score { get; set; }
    public bool NeedsConfig { get; set; }
    public string InstallSpec { get; set; } = "";

    /// <summary>该插件来自哪些来源（来源名称，去重有序）。</summary>
    public List<string> Sources { get; set; } = new();
}

/// <summary>插件列表持久化文件（单一文件，JSON 格式）。</summary>
public sealed class PluginCacheFile
{
    public int Version { get; set; } = 1;
    public string SavedAt { get; set; } = "";
    public List<string> Sources { get; set; } = new();
    public List<PluginCacheEntry> Plugins { get; set; } = new();
}

/// <summary>
/// 把整合后的插件列表持久化到 <c>%APPDATA%\DshDesktop\plugins-cache.json</c>（唯一缓存文件），
/// 以便全部插件源不可用时仍能展示上次成功获取的插件列表。
/// </summary>
public static class PluginCache
{
    private const int CacheVersion = 1;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DshDesktop",
        "plugins-cache.json");

    /// <summary>保存（覆盖写）插件缓存。失败静默，不影响主流程。</summary>
    public static async Task SaveAsync(PluginCacheFile cache)
    {
        try
        {
            cache.Version = CacheVersion;
            cache.SavedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz");
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(FilePath, json);
        }
        catch
        {
            // 缓存写失败不影响应用运行
        }
    }

    /// <summary>读取插件缓存；不存在或损坏返回 null。</summary>
    public static async Task<PluginCacheFile?> LoadAsync()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }
            var json = await File.ReadAllTextAsync(FilePath);
            var cache = JsonSerializer.Deserialize<PluginCacheFile>(json);
            return cache is { Plugins.Count: > 0 } ? cache : null;
        }
        catch
        {
            return null;
        }
    }
}

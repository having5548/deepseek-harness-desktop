using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DshDesktop.Services;

/// <summary>插件来源类型。</summary>
public enum PluginSourceKind
{
    /// <summary>DSH Market 风格的 JSON 数据文件。</summary>
    MarketJson,

    /// <summary>npm registry 搜索接口。</summary>
    NpmSearch,
}

/// <summary>一个可选的插件来源。</summary>
public sealed record PluginSource(string Id, string Name, string Url, PluginSourceKind Kind);

/// <summary>来自某个来源的原始插件条目。</summary>
public sealed record RemotePlugin(
    string PackageName,
    string RepoFullName,
    string RepoUrl,
    string Author,
    string? Description,
    int Stars,
    int Score,
    bool NeedsConfig,
    string InstallSpec,
    string SourceName);

/// <summary>多来源获取的结果。</summary>
public sealed record PluginFetchResult(IReadOnlyList<RemotePlugin> Plugins, IReadOnlyList<string> FailedSources);

/// <summary>dsh plugin 命令执行结果。</summary>
public sealed record PluginCommandResult(bool Success, string Output);

/// <summary>
/// 插件管理：
/// <list type="bullet">
/// <item>从多个可信来源（DSH Market / npm 官方 / npmmirror）拉取插件并去重合并；</item>
/// <item>通过捆绑 dsh 的 <c>plugin --profile web add/remove</c> 安装 / 卸载插件；</item>
/// <item>维护崩溃后自动屏蔽的插件清单（持久化在 <see cref="AppSettings"/>）。</item>
/// </list>
/// </summary>
public static class PluginManager
{
    /// <summary>DSH Market 数据源（Web 站与插件版共用，每日更新）。</summary>
    private const string DshMarketDataUrl =
        "https://raw.githubusercontent.com/2BingLing/dsh-market/master/data/plugins.json";

    private const int MaxPerSource = 100;
    private const int MaxMergedPlugins = 150;

    /// <summary>内置可信插件来源（官方 / 官方镜像，无来历不明者）。</summary>
    public static readonly PluginSource[] AllSources =
    {
        new("dsh-market", "DSH Market", DshMarketDataUrl, PluginSourceKind.MarketJson),
        new("npm", "npm 官方", "https://registry.npmjs.org/-/v1/search?text=keywords:dsh-plugin&size=" + MaxPerSource, PluginSourceKind.NpmSearch),
        new("npmmirror", "npmmirror 镜像", "https://registry.npmmirror.com/-/v1/search?text=dsh-plugin&size=" + MaxPerSource, PluginSourceKind.NpmSearch),
    };

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-desktop");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    public static PluginSource? FindSource(string id)
        => AllSources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 依次从给定来源拉取插件（每个来源独立容错，单源失败不影响其他源）。
    /// </summary>
    public static async Task<PluginFetchResult> FetchAsync(IEnumerable<PluginSource> sources)
    {
        var plugins = new List<RemotePlugin>();
        var failed = new List<string>();
        foreach (var source in sources)
        {
            try
            {
                var list = source.Kind == PluginSourceKind.MarketJson
                    ? await FetchMarketAsync(source)
                    : await FetchNpmSearchAsync(source);
                plugins.AddRange(list);
            }
            catch
            {
                failed.Add(source.Name);
            }
        }
        return new PluginFetchResult(plugins, failed);
    }

    /// <summary>按「GitHub 仓库链接 + 作者」去重合并多来源条目，取各来源最优信息，按实用分排序。</summary>
    public static List<PluginCacheEntry> MergeAndDedupe(IEnumerable<RemotePlugin> raw)
    {
        var merged = new Dictionary<string, PluginCacheEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in raw)
        {
            var key = DedupKey(p);
            if (key is null)
            {
                continue;
            }
            if (merged.TryGetValue(key, out var entry))
            {
                merged[key] = Merge(entry, p);
            }
            else
            {
                merged[key] = ToEntry(p);
            }
        }
        return merged.Values
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Stars)
            .Take(MaxMergedPlugins)
            .ToList();
    }

    /// <summary>去重键：GitHub 仓库 URL（小写），无仓库时退回包名。</summary>
    private static string? DedupKey(RemotePlugin p)
    {
        var repo = NormalizeRepoUrl(p.RepoUrl);
        if (repo is not null)
        {
            return "repo:" + repo.ToLowerInvariant();
        }
        var name = p.PackageName?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(name) ? null : "pkg:" + name;
    }

    private static PluginCacheEntry ToEntry(RemotePlugin p) => new()
    {
        PackageName = p.PackageName,
        RepoFullName = p.RepoFullName,
        RepoUrl = p.RepoUrl,
        Author = p.Author,
        Description = p.Description,
        Stars = p.Stars,
        Score = p.Score,
        NeedsConfig = p.NeedsConfig,
        InstallSpec = p.InstallSpec,
        Sources = { p.SourceName },
    };

    /// <summary>合并两个来源的同一条插件：信息取优，来源并集。</summary>
    private static PluginCacheEntry Merge(PluginCacheEntry a, RemotePlugin b)
    {
        var merged = new PluginCacheEntry
        {
            PackageName = string.IsNullOrEmpty(a.PackageName) ? b.PackageName : a.PackageName,
            RepoFullName = string.IsNullOrEmpty(a.RepoFullName) ? b.RepoFullName : a.RepoFullName,
            RepoUrl = string.IsNullOrEmpty(a.RepoUrl) ? b.RepoUrl : a.RepoUrl,
            Author = string.IsNullOrEmpty(a.Author) ? b.Author : a.Author,
            Description = string.IsNullOrEmpty(a.Description) ? b.Description : a.Description,
            Stars = Math.Max(a.Stars, b.Stars),
            Score = Math.Max(a.Score, b.Score),
            NeedsConfig = a.NeedsConfig || b.NeedsConfig,
            InstallSpec = string.IsNullOrEmpty(a.InstallSpec) ? b.InstallSpec : a.InstallSpec,
        };
        merged.Sources.AddRange(a.Sources);
        if (!merged.Sources.Contains(b.SourceName))
        {
            merged.Sources.Add(b.SourceName);
        }
        return merged;
    }

    /// <summary>把各类仓库链接规范化为 https://github.com/owner/repo 形式；无法识别返回 null。</summary>
    public static string? NormalizeRepoUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var s = raw.Trim();
        if (s.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
        {
            s = s[4..];
        }
        if (s.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            s = "https://github.com/" + s[7..];
        }
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            var path = uri.AbsolutePath.Trim('/').TrimEnd('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^4];
            }
            return string.IsNullOrEmpty(path) ? null : "https://github.com/" + path;
        }
        // 裸的 owner/repo
        var parts = s.Split('/');
        if (parts.Length == 2
            && parts[0].Length > 0
            && parts[1].Length > 0
            && !s.Contains(' ', StringComparison.Ordinal))
        {
            return "https://github.com/" + s.TrimEnd('/');
        }
        return null;
    }

    /// <summary>从仓库 URL / fullName 提取作者（owner）。</summary>
    public static string? ExtractAuthor(string? repoUrl, string? repoFullName)
    {
        var repo = NormalizeRepoUrl(repoUrl) ?? NormalizeRepoUrl(repoFullName);
        if (repo is not null)
        {
            var path = repo["https://github.com/".Length..];
            var idx = path.IndexOf('/');
            if (idx > 0)
            {
                return path[..idx];
            }
        }
        return null;
    }

    // ── 来源解析 ───────────────────────────────────────────

    private static async Task<List<RemotePlugin>> FetchMarketAsync(PluginSource source)
    {
        var plugins = new List<RemotePlugin>();
        var json = await Http.GetStringAsync(source.Url);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("plugins", out var items))
        {
            return plugins;
        }
        foreach (var item in items.EnumerateArray())
        {
            var plugin = ParseMarketItem(item, source);
            if (plugin is not null)
            {
                plugins.Add(plugin);
            }
        }
        return plugins
            .OrderByDescending(p => p.Score)
            .ThenByDescending(p => p.Stars)
            .Take(MaxPerSource)
            .ToList();
    }

    private static RemotePlugin? ParseMarketItem(JsonElement item, PluginSource source)
    {
        var name = GetString(item, "name");
        var fullName = GetString(item, "fullName") ?? GetString(item, "id");
        var description = GetString(item, "descriptionZh") ?? GetString(item, "description");
        var stars = GetInt(item, "stars");
        var score = GetInt(item, "score", "total");
        var needsConfig = GetBool(item, "install", "needsConfig");
        var installSpec = ParseInstallSpec(item);

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installSpec))
        {
            return null; // 无法通过 `dsh plugin add` 安装的（如 skill 型）跳过
        }

        var repoUrl = NormalizeRepoUrl(fullName);
        return new RemotePlugin(
            name ?? string.Empty,
            fullName ?? string.Empty,
            repoUrl ?? string.Empty,
            ExtractAuthor(repoUrl, fullName) ?? string.Empty,
            description,
            stars,
            score,
            needsConfig,
            installSpec,
            source.Name);
    }

    private static async Task<List<RemotePlugin>> FetchNpmSearchAsync(PluginSource source)
    {
        var plugins = new List<RemotePlugin>();
        var json = await Http.GetStringAsync(source.Url);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("objects", out var objects))
        {
            return plugins;
        }
        foreach (var obj in objects.EnumerateArray())
        {
            if (!obj.TryGetProperty("package", out var pkg))
            {
                continue;
            }
            var name = GetString(pkg, "name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            var description = GetString(pkg, "description");
            var repoUrl = NormalizeRepoUrl(GetString(pkg, "links", "repository"));
            var fullName = repoUrl?["https://github.com/".Length..];
            var score = 0;
            if (obj.TryGetProperty("score", out var scoreEl)
                && scoreEl.TryGetProperty("final", out var final)
                && final.ValueKind == JsonValueKind.Number)
            {
                score = (int)Math.Round(final.GetDouble() * 100);
            }
            plugins.Add(new RemotePlugin(
                name,
                fullName ?? string.Empty,
                repoUrl ?? string.Empty,
                ExtractAuthor(repoUrl, fullName) ?? string.Empty,
                description,
                0,
                score,
                false,
                name,
                source.Name));
        }
        return plugins
            .OrderByDescending(p => p.Score)
            .Take(MaxPerSource)
            .ToList();
    }

    /// <summary>从 <c>install.commands</c> 提取 <c>dsh plugin ... add &lt;spec&gt;</c> 中的安装包 spec（支持 add 后带选项）。</summary>
    private static string? ParseInstallSpec(JsonElement item)
    {
        if (!item.TryGetProperty("install", out var install)
            || !install.TryGetProperty("commands", out var commands)
            || commands.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var cmd in commands.EnumerateArray())
        {
            var text = cmd.GetString();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }
            var match = Regex.Match(text, @"\badd\s+(?:-\S+\s+)*(\S+)");
            if (match.Success)
            {
                var spec = match.Groups[1].Value.Trim().Trim('"', '\'');
                if (spec.Length > 0)
                {
                    return spec;
                }
            }
        }
        return null;
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var p in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(p, out current))
            {
                return null;
            }
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static int GetInt(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var p in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(p, out current))
            {
                return 0;
            }
        }
        return current.ValueKind == JsonValueKind.Number ? current.GetInt32() : 0;
    }

    private static bool GetBool(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var p in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(p, out current))
            {
                return false;
            }
        }
        return current.ValueKind == JsonValueKind.True;
    }

    /// <summary>执行 <c>dsh plugin --profile web &lt;args...&gt;</c>，并把捆绑运行时目录注入 PATH 以便找到 pnpm。</summary>
    public static async Task<PluginCommandResult> RunPluginCommandAsync(params string[] args)
    {
        var runtime = await DshLocator.FindAsync(null);
        if (runtime is null)
        {
            return new PluginCommandResult(false, "未找到 dsh 运行时。");
        }
        if (string.IsNullOrEmpty(runtime.ScriptPath))
        {
            return new PluginCommandResult(false, "插件管理需要 node + bin.js 运行方式。");
        }

        var psi = new ProcessStartInfo
        {
            FileName = runtime.NodePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(runtime.ScriptPath);
        psi.ArgumentList.Add("plugin");
        psi.ArgumentList.Add("--profile");
        psi.ArgumentList.Add("web");
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        // 注入 PATH：`dsh plugin` 内部 spawnSync('pnpm') 依赖 PATH 找到捆绑的 pnpm
        var runtimeDir = Path.GetDirectoryName(runtime.NodePath);
        var path = Environment.GetEnvironmentVariable("Path") ?? string.Empty;
        psi.Environment["Path"] = string.IsNullOrEmpty(runtimeDir) ? path : runtimeDir + ";" + path;

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return new PluginCommandResult(false, "无法启动 dsh plugin 进程。");
            }
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var exitCode = await Task.Run(() => { proc.WaitForExit(); return proc.ExitCode; });
            var output = $"{await stdoutTask}\n{await stderrTask}".Trim();
            return new PluginCommandResult(exitCode == 0, output);
        }
        catch (Exception ex)
        {
            return new PluginCommandResult(false, ex.Message);
        }
    }

    /// <summary>安装插件。返回是否成功及输出。</summary>
    public static Task<PluginCommandResult> InstallAsync(string packageName)
        => RunPluginCommandAsync("add", packageName);

    /// <summary>卸载插件。返回是否成功及输出。</summary>
    public static Task<PluginCommandResult> RemoveAsync(string packageName)
        => RunPluginCommandAsync("remove", packageName);

    /// <summary>屏蔽插件：卸载并从设置中记录，返回是否成功。</summary>
    public static async Task<bool> DisablePluginAsync(AppSettings settings, string packageName)
    {
        if (settings.DisabledPlugins.Contains(packageName))
        {
            return true;
        }
        var result = await RemoveAsync(packageName);
        if (result.Success)
        {
            settings.DisabledPlugins.Add(packageName);
            settings.Save();
        }
        return result.Success;
    }

    /// <summary>恢复插件：重新安装并移除屏蔽记录，返回是否成功。</summary>
    public static async Task<bool> EnablePluginAsync(AppSettings settings, string packageName)
    {
        var result = await InstallAsync(packageName);
        if (result.Success)
        {
            settings.DisabledPlugins.Remove(packageName);
            settings.Save();
        }
        return result.Success;
    }

    /// <summary>读取 web profile 中已安装的插件（dsh.profile.bundles），含模板内置 bundle。</summary>
    public static async Task<List<string>> GetInstalledPluginsAsync()
    {
        var list = new List<string>();
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var manifest = Path.Combine(home, ".dsh", "profiles", "web", "package.json");
            if (!File.Exists(manifest))
            {
                return list;
            }
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
            if (doc.RootElement.TryGetProperty("dsh", out var dsh)
                && dsh.TryGetProperty("profile", out var profile)
                && profile.TryGetProperty("bundles", out var bundles))
            {
                foreach (var item in bundles.EnumerateArray())
                {
                    var name = item.GetString();
                    if (!string.IsNullOrEmpty(name) && !list.Contains(name))
                    {
                        list.Add(name);
                    }
                }
            }
        }
        catch
        {
            // manifest 不存在或损坏时返回空
        }
        return list;
    }
}

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

/// <summary>远程插件信息（来自 DSH Market）。</summary>
public sealed record RemotePlugin(
    string PackageName,
    string RepoFullName,
    string? Description,
    int Stars,
    int Score,
    bool NeedsConfig,
    string InstallSpec);

/// <summary>dsh plugin 命令执行结果。</summary>
public sealed record PluginCommandResult(bool Success, string Output);

/// <summary>
/// 插件管理：
/// <list type="bullet">
/// <item>从 GitHub 的 <c>dsh-plugin</c> 主题发现插件（读取各仓库 package.json 得到真实 npm 包名）；</item>
/// <item>通过捆绑 dsh 的 <c>plugin --profile web add/remove</c> 安装 / 卸载插件；</item>
/// <item>维护崩溃后自动屏蔽的插件清单（持久化在 <see cref="AppSettings"/>）。</item>
/// </list>
/// </summary>
public static class PluginManager
{
    /// <summary>DSH Market 数据源（Web 站与插件版共用，每日更新）。</summary>
    private const string DshMarketDataUrl =
        "https://raw.githubusercontent.com/2BingLing/dsh-market/master/data/plugins.json";

    /// <summary>列表中最多展示的插件数（按实用分取高分）。</summary>
    private const int MaxPlugins = 100;

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-desktop");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    /// <summary>
    /// 从 DSH Market 拉取可一键安装的插件：解析 <c>install.commands</c> 中的
    /// <c>dsh plugin add &lt;spec&gt;</c> 得到安装包，按实用分排序取高分前 <see cref="MaxPlugins"/> 个。
    /// </summary>
    public static async Task<List<RemotePlugin>> FetchRemotePluginsAsync()
    {
        var plugins = new List<RemotePlugin>();
        try
        {
            var json = await Http.GetStringAsync(DshMarketDataUrl);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("plugins", out var items))
            {
                return plugins;
            }

            foreach (var item in items.EnumerateArray())
            {
                var plugin = ParseRemotePlugin(item);
                if (plugin is not null)
                {
                    plugins.Add(plugin);
                }
            }
        }
        catch
        {
            // 网络失败时返回已收集的部分结果
        }
        return plugins
            .OrderByDescending(p => p.Score)
            .ThenByDescending(p => p.Stars)
            .Take(MaxPlugins)
            .ToList();
    }

    private static RemotePlugin? ParseRemotePlugin(JsonElement item)
    {
        var name = GetString(item, "name");
        var fullName = GetString(item, "fullName") ?? GetString(item, "id");
        var descriptionZh = GetString(item, "descriptionZh");
        var description = GetString(item, "description");
        var stars = GetInt(item, "stars");
        var score = GetInt(item, "score", "total");
        var needsConfig = GetBool(item, "install", "needsConfig");
        var installSpec = ParseInstallSpec(item);

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installSpec))
        {
            return null; // 无法通过 `dsh plugin add` 安装的（如 skill 型）跳过
        }
        return new RemotePlugin(
            name,
            fullName ?? name,
            descriptionZh ?? description,
            stars,
            score,
            needsConfig,
            installSpec);
    }

    /// <summary>从 <c>install.commands</c> 提取 <c>dsh plugin ... add &lt;spec&gt;</c> 中的安装包 spec。</summary>
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
            var match = Regex.Match(text, @"\badd\s+(\S+)");
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

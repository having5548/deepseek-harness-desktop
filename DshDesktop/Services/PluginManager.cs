using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DshDesktop.Services;

/// <summary>远程插件信息（来自 GitHub `dsh-plugin` 主题）。</summary>
public sealed record RemotePlugin(string PackageName, string RepoFullName, string? Description, int Stars);

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
    private const string GitHubSearchUrl =
        "https://api.github.com/search/repositories?q=topic:dsh-plugin&sort=stars&order=desc&per_page=50";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-desktop");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    /// <summary>从 GitHub 发现 dsh 插件列表。会读取每个仓库的 package.json 以获取真实 npm 包名。</summary>
    public static async Task<List<RemotePlugin>> FetchRemotePluginsAsync()
    {
        var plugins = new List<RemotePlugin>();
        try
        {
            var search = await Http.GetStringAsync(GitHubSearchUrl);
            using var doc = JsonDocument.Parse(search);
            if (!doc.RootElement.TryGetProperty("items", out var items))
            {
                return plugins;
            }

            // 并行读取 package.json，避免串行拖慢
            var tasks = items.EnumerateArray().Select(async item =>
            {
                var fullName = item.TryGetProperty("full_name", out var fn) ? fn.GetString() : null;
                var description = item.TryGetProperty("description", out var d) ? d.GetString() : null;
                var stars = item.TryGetProperty("stargazers_count", out var s) ? s.GetInt32() : 0;
                var branch = item.TryGetProperty("default_branch", out var b) ? b.GetString() ?? "main" : "main";
                if (string.IsNullOrEmpty(fullName) || fullName.Equals("deepseek-ai/deepseek-harness", StringComparison.OrdinalIgnoreCase))
                {
                    return (RemotePlugin?)null;
                }
                var pkgName = await TryReadPackageNameAsync(fullName, branch);
                return string.IsNullOrEmpty(pkgName) ? null : new RemotePlugin(pkgName, fullName, description, stars);
            }).ToList();

            foreach (var task in tasks)
            {
                var result = await task;
                if (result is not null)
                {
                    plugins.Add(result);
                }
            }
        }
        catch
        {
            // 网络失败时返回已收集的部分结果
        }
        return plugins;
    }

    private static async Task<string?> TryReadPackageNameAsync(string fullName, string branch)
    {
        try
        {
            var url = $"https://raw.githubusercontent.com/{fullName}/{branch}/package.json";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("name", out var name))
            {
                return name.GetString();
            }
        }
        catch
        {
            // 仓库无 package.json 或不可访问 → 不是可安装插件
        }
        return null;
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
}

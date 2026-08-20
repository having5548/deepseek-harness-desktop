using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DshDesktop.Services;

/// <summary>一次 dsh 版本检测的结果。</summary>
public sealed record DshUpdateInfo(string LocalVersion, string? LatestVersion)
{
    /// <summary>远程存在比本地更新的版本。</summary>
    public bool IsUpdateAvailable =>
        !string.IsNullOrEmpty(LatestVersion)
        && DshUpdater.CompareVersions(LatestVersion!, LocalVersion) > 0;
}

/// <summary>
/// dsh 运行时升级：
/// <list type="bullet">
/// <item>读取本地捆绑 dsh 版本（runtime/node_modules/@deepseek-ai/dsh）；</item>
/// <item>查询 npm registry 的 dist-tags（latest / next），取较新者；</item>
/// <item>用捆绑的 npm 把 dsh 重装到捆绑运行时，实现应用内自升级。</item>
/// </list>
/// </summary>
public static class DshUpdater
{
    private const string PackageName = "@deepseek-ai/dsh";
    private const string DistTagsUrl = "https://registry.npmjs.org/-/package/@deepseek-ai/dsh/dist-tags";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-desktop");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    /// <summary>本地捆绑 dsh 的版本号；读取失败返回 null。</summary>
    public static string? GetLocalVersion()
    {
        try
        {
            var pkg = Path.Combine(
                AppContext.BaseDirectory, "runtime", "node_modules",
                "@deepseek-ai", "dsh", "package.json");
            if (!File.Exists(pkg))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(pkg));
            return doc.RootElement.TryGetProperty("version", out var v)
                ? v.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>查询 npm registry，返回本地版本与远程最新版本（latest / next 中较新者）。网络失败视为无更新。</summary>
    public static async Task<DshUpdateInfo> CheckForUpdateAsync()
    {
        var local = GetLocalVersion() ?? "0.0.0";
        string? remote = null;
        try
        {
            var json = await Http.GetStringAsync(DistTagsUrl);
            using var doc = JsonDocument.Parse(json);
            foreach (var tag in new[] { "latest", "next" })
            {
                if (doc.RootElement.TryGetProperty(tag, out var node)
                    && node.ValueKind == JsonValueKind.String)
                {
                    remote = PickNewer(remote, node.GetString());
                }
            }
        }
        catch
        {
            // 网络失败：视为无更新
        }
        return new DshUpdateInfo(local, remote);
    }

    /// <summary>用捆绑 npm 把 dsh 升级到指定版本。返回是否成功与完整输出。</summary>
    public static async Task<(bool Success, string Output)> UpgradeAsync(string version)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var node = Path.Combine(baseDir, "runtime", "node.exe");
            var npmCli = Path.Combine(baseDir, "runtime", "node_modules", "npm", "bin", "npm-cli.js");
            var runtimeDir = Path.Combine(baseDir, "runtime");
            if (!File.Exists(node) || !File.Exists(npmCli))
            {
                return (false, "运行时缺少 npm（未捆绑），无法自升级。\n可运行 scripts\\prepare-runtime.cmd 重新生成运行时。");
            }

            var psi = new ProcessStartInfo
            {
                FileName = node,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = runtimeDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(npmCli);
            psi.ArgumentList.Add("install");
            psi.ArgumentList.Add("-g");
            psi.ArgumentList.Add("--prefix");
            psi.ArgumentList.Add(runtimeDir);
            psi.ArgumentList.Add("--omit=dev");
            psi.ArgumentList.Add("--no-audit");
            psi.ArgumentList.Add("--no-fund");
            psi.ArgumentList.Add("--allow-scripts=@deepseek-ai/dsh-subprocess-local,koffi,node-pty,@google/genai,protobufjs");
            psi.ArgumentList.Add($"{PackageName}@{version}");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, "无法启动 npm 升级进程。");
            }
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var exitCode = await Task.Run(() => { proc.WaitForExit(); return proc.ExitCode; });
            var output = $"{await stdoutTask}\n{await stderrTask}".Trim();
            return (exitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── 版本比较（简化 semver：core + prerelease）────────────────

    private static string? PickNewer(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a)) return b;
        if (string.IsNullOrEmpty(b)) return a;
        return CompareVersions(a, b) >= 0 ? a : b;
    }

    public static int CompareVersions(string a, string b)
    {
        var (ma, pa) = SplitVersion(a);
        var (mb, pb) = SplitVersion(b);
        for (var i = 0; i < 3; i++)
        {
            var cmp = ma[i].CompareTo(mb[i]);
            if (cmp != 0) return cmp;
        }
        // core 相同：无预发布号视为比预发布版新
        if (string.IsNullOrEmpty(pa) && string.IsNullOrEmpty(pb)) return 0;
        if (string.IsNullOrEmpty(pa)) return 1;
        if (string.IsNullOrEmpty(pb)) return -1;
        return ComparePrerelease(pa, pb);
    }

    private static (int[] Core, string Pre) SplitVersion(string v)
    {
        var core = v;
        var pre = string.Empty;
        var dash = v.IndexOf('-');
        if (dash >= 0)
        {
            core = v[..dash];
            pre = v[(dash + 1)..];
        }
        var nums = core.Split('.');
        var arr = new int[3];
        for (var i = 0; i < 3; i++)
        {
            arr[i] = i < nums.Length && int.TryParse(nums[i], out var x) ? x : 0;
        }
        return (arr, pre);
    }

    private static int ComparePrerelease(string a, string b)
    {
        var as_ = a.Split('.');
        var bs = b.Split('.');
        var n = Math.Max(as_.Length, bs.Length);
        for (var i = 0; i < n; i++)
        {
            if (i >= as_.Length) return -1;
            if (i >= bs.Length) return 1;
            var av = as_[i];
            var bv = bs[i];
            if (av == bv) continue;
            var aIsNum = int.TryParse(av, out var an);
            var bIsNum = int.TryParse(bv, out var bn);
            if (aIsNum && bIsNum) return an.CompareTo(bn);
            if (aIsNum) return -1; // 数字标识 < 字母标识
            if (bIsNum) return 1;
            return string.CompareOrdinal(av, bv);
        }
        return 0;
    }
}

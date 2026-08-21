using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DshDesktop.Services;

/// <summary>一次 dsh 版本检测的结果。</summary>
public sealed record DshUpdateInfo(
    string LocalVersion,
    string? LatestVersion,
    string? RegistryName = null,
    string? RegistryUrl = null,
    long? LatencyMs = null)
{
    /// <summary>远程存在比本地更新的版本。</summary>
    public bool IsUpdateAvailable =>
        !string.IsNullOrEmpty(LatestVersion)
        && DshUpdater.CompareVersions(LatestVersion!, LocalVersion) > 0;
}

/// <summary>一次升级的结果。</summary>
public sealed record DshUpgradeResult(bool Success, bool Cancelled, string Output);

/// <summary>可用 npm 源及其测得的延迟与 dist-tags。</summary>
public sealed record DshRegistry(string Name, string Url, long LatencyMs, JsonElement DistTags);

/// <summary>
/// dsh 运行时升级：
/// <list type="bullet">
/// <item>读取本地捆绑 dsh 版本（runtime/node_modules/@deepseek-ai/dsh）；</item>
/// <item>对多个 npm 源（官方 + 国内镜像）自动 ping，选延迟最低者查询 dist-tags；</item>
/// <item>用捆绑的 npm 以所选源把 dsh 重装到捆绑运行时，支持进度、取消与超时。</item>
/// </list>
/// </summary>
public static class DshUpdater
{
    private const string PackageName = "@deepseek-ai/dsh";
    private const string DistTagsPath = "-/package/@deepseek-ai/dsh/dist-tags";

    /// <summary>候选 npm 源（官方 + 国内镜像，规避网络不可达/被墙）。</summary>
    private static readonly (string Name, string Url)[] Registries =
    {
        ("npm 官方", "https://registry.npmjs.org/"),
        ("npmmirror", "https://registry.npmmirror.com/"),
        ("腾讯云镜像", "https://mirrors.cloud.tencent.com/npm/"),
        ("华为云镜像", "https://registry.huaweicloud.com/repository/npm/"),
    };

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-desktop");
        client.Timeout = TimeSpan.FromSeconds(8);
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

    /// <summary>
    /// 对候选源并行 ping（请求其 dist-tags 端点），返回延迟最低且可用的源；
    /// 全部不可达返回 null。同时也拿到该源的 dist-tags，避免重复请求。
    /// </summary>
    public static async Task<DshRegistry?> SelectBestRegistryAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var tasks = Registries.Select(async r =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var json = await Http.GetStringAsync(r.Url + DistTagsPath, cts.Token);
                sw.Stop();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }
                return new DshRegistry(r.Name, r.Url, sw.ElapsedMilliseconds, doc.RootElement.Clone());
            }
            catch
            {
                return null;
            }
        });
        var results = await Task.WhenAll(tasks);
        return results
            .Where(x => x is not null)
            .OrderBy(x => x!.LatencyMs)
            .FirstOrDefault();
    }

    /// <summary>
    /// 查询最优源，返回本地版本与远程最新版本（latest / next 中较新者）。
    /// 所有源均不可达时 <see cref="DshUpdateInfo.RegistryName"/> 为 null，视为无法检查。
    /// </summary>
    public static async Task<DshUpdateInfo> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var local = GetLocalVersion() ?? "0.0.0";
        var registry = await SelectBestRegistryAsync(ct);
        string? remote = null;
        if (registry is not null)
        {
            foreach (var tag in new[] { "latest", "next" })
            {
                if (registry.DistTags.TryGetProperty(tag, out var node)
                    && node.ValueKind == JsonValueKind.String)
                {
                    remote = PickNewer(remote, node.GetString());
                }
            }
        }
        return new DshUpdateInfo(local, remote, registry?.Name, registry?.Url, registry?.LatencyMs);
    }

    /// <summary>
    /// 用捆绑 npm 以指定源把 dsh 升级到指定版本。
    /// 实时回调 npm 输出行；<paramref name="ct"/> 取消会终止整棵进程树；
    /// 内置 15 分钟超时兜底，避免网络卡死导致无限挂起。
    /// </summary>
    public static async Task<DshUpgradeResult> UpgradeAsync(
        string version, string registryUrl, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var node = Path.Combine(baseDir, "runtime", "node.exe");
            var npmCli = Path.Combine(baseDir, "runtime", "node_modules", "npm", "bin", "npm-cli.js");
            var runtimeDir = Path.Combine(baseDir, "runtime");
            if (!File.Exists(node) || !File.Exists(npmCli))
            {
                return new DshUpgradeResult(false, false,
                    "运行时缺少 npm（未捆绑），无法自升级。\n可运行 scripts\\prepare-runtime.cmd 重新生成运行时。");
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
            psi.ArgumentList.Add("--registry");
            psi.ArgumentList.Add(registryUrl);
            psi.ArgumentList.Add("--omit=dev");
            psi.ArgumentList.Add("--no-audit");
            psi.ArgumentList.Add("--no-fund");
            psi.ArgumentList.Add("--allow-scripts=@deepseek-ai/dsh-subprocess-local,koffi,node-pty,@google/genai,protobufjs");
            psi.ArgumentList.Add($"{PackageName}@{version}");

            // 15 分钟超时兜底：网络卡死时自动终止，避免无限挂起
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(15));
            var token = timeoutCts.Token;

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return new DshUpgradeResult(false, false, "无法启动 npm 升级进程。");
            }

            var output = new StringBuilder();
            async Task PumpAsync(StreamReader reader)
            {
                try
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync(token)) is not null)
                    {
                        lock (output)
                        {
                            if (output.Length > 0) output.Append('\n');
                            output.Append(line);
                        }
                        progress?.Report(line);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 取消/超时：读流被中断，交给下方退出分支统一处理
                }
            }

            var pumpOut = PumpAsync(proc.StandardOutput);
            var pumpErr = PumpAsync(proc.StandardError);

            var cancelled = false;
            var exitCode = -1;
            try
            {
                exitCode = await Task.Run(() =>
                {
                    using var reg = token.Register(() =>
                    {
                        cancelled = true;
                        try { proc.Kill(entireProcessTree: true); } catch { }
                    });
                    proc.EnableRaisingEvents = true;
                    var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    proc.Exited += (_, _) => tcs.TrySetResult(proc.ExitCode);
                    if (proc.HasExited) tcs.TrySetResult(proc.ExitCode);
                    return tcs.Task;
                }, token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                exitCode = -1;
            }

            await Task.WhenAll(pumpOut, pumpErr);

            var text = output.ToString().Trim();
            if (cancelled)
            {
                return new DshUpgradeResult(false, true,
                    ct.IsCancellationRequested ? "升级已取消。" : "升级超时（15 分钟），已中止。");
            }
            return new DshUpgradeResult(exitCode == 0, false, text);
        }
        catch (OperationCanceledException)
        {
            return new DshUpgradeResult(false, true, "升级已取消。");
        }
        catch (Exception ex)
        {
            return new DshUpgradeResult(false, false, ex.Message);
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

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
/// dsh 运行时安装 / 升级：
/// <list type="bullet">
/// <item>读取本地 dsh 版本（DshPaths.DshPackageJson，未安装返回 null）；</item>
/// <item>对多个 npm 源（官方 + 国内镜像）自动 ping，选延迟最低者查询 dist-tags；</item>
/// <item>用捆绑的 npm 以所选源把 dsh 安装到安装根目录（DshPaths.InstallRoot），
///     支持进度、取消与超时；首次启动的自动安装与手动升级共用此逻辑。</item>
/// </list>
/// </summary>
public static class DshUpdater
{
    private const string DistTagsPath = "-/package/@deepseek-ai/dsh/dist-tags";

    /// <summary>候选 npm 源（官方 + 国内镜像，规避网络不可达/被墙）。</summary>
    private static readonly (string Name, string Url)[] Registries =
    {
        ("npm 官方", "https://registry.npmjs.org/"),
        ("npmmirror", "https://registry.npmmirror.com/"),
        ("腾讯云镜像", "https://mirrors.cloud.tencent.com/npm/"),
        // 注意：华为云 npm 镜像是 repo.huaweicloud.com，写成 registry.huaweicloud.com 无法解析
        ("华为云镜像", "https://repo.huaweicloud.com/repository/npm/"),
    };

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-desktop");
        client.Timeout = TimeSpan.FromSeconds(8);
        return client;
    }

    /// <summary>本地 dsh 的版本号；读取失败或未安装返回 null。</summary>
    public static string? GetLocalVersion()
    {
        try
        {
            var pkg = DshPaths.DshPackageJson;
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
        // 解析出"确切版本号"（latest 与 next 中较新者）。用户看到的目标版本必须与最终交给
        // npm 安装的版本号完全一致 —— 绝不能把 latest/next 这类标签直接用于安装，否则会出现
        // "CLI 停在旧版、插件包却升到新版"的版本偏斜。
        var remote = registry is null ? null : DshInstaller.ResolveVersion(registry);
        return new DshUpdateInfo(local, remote, registry?.Name, registry?.Url, registry?.LatencyMs);
    }

    /// <summary>
    /// 安装 / 升级 dsh。<paramref name="version"/> 必须是<b>确切版本号</b>（如 <c>0.1.5-rc.2</c>），
    /// 不接受 <c>latest</c> / <c>next</c> 标签 —— 原因见 <see cref="DshInstaller"/> 的类说明。
    /// 实际安装由 <see cref="DshInstaller.InstallAsync"/> 以"暂存安装 + 校验 + 整体替换"的方式完成，
    /// 避免就地更新留下半新半旧的坏安装。
    /// </summary>
    public static Task<DshUpgradeResult> UpgradeAsync(
        string version, string registryUrl, IProgress<string>? progress, CancellationToken ct)
        => DshInstaller.InstallAsync(version, registryUrl, progress, ct);

    // ── 版本比较（简化 semver：core + prerelease）────────────────

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

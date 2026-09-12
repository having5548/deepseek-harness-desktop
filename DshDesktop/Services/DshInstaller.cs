using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DshDesktop.Services;

/// <summary>
/// dsh 安装 / 升级执行器：把 npm 安装结果先写到独立暂存目录，校验通过后再整体替换到安装根目录。
///
/// <para><b>为什么不再就地 npm install：</b>
/// 过去直接在安装根目录上跑 <c>npm install</c>，若旧目录里有被 dsh 进程占用（Windows 文件锁）
/// 或新版本已删除的文件，npm 会跳过后继续 —— 于是留下"半新半旧"的坏安装：CLI 还是旧版、
/// 插件包却已升级，启动即报错，只能手动删光整个目录重装。
/// 现在改为 暂存安装 → 版本校验 → 整体替换，失败则原样回滚，等价于自动完成"删干净重装"。</para>
///
/// <para><b>为什么必须锁死确切版本号：</b>
/// dsh 的 npm 标签 <c>latest</c>（0.1.5-rc.1）可能比 <c>next</c>（0.1.5-rc.2）更旧，而它声明的
/// 依赖范围 <c>^0.1.5-rc.1</c> 会被 npm 解析到该范围内最新的 <c>0.1.5-rc.2</c>。
/// 若把 <c>latest</c> 这个标签直接交给 npm，就会得到"dsh CLI 停在 rc.1、所有插件包升到 rc.2"的
/// <b>版本偏斜</b>，运行时插件（dsh-web-app / dsh-base 等）随即加载失败。
/// 因此这里只接受确切版本号，并由 <see cref="ResolveVersion"/> 从 dist-tags 解析出最新者再安装。</para>
/// </summary>
public static class DshInstaller
{
    private const string PackageName = "@deepseek-ai/dsh";

    /// <summary>安装失败时回传给用户界面的日志尾部行数。</summary>
    private const int ErrorTailLines = 20;

    /// <summary>npm 全局安装在 prefix 根目录生成的转发脚本（内容为相对路径，可整体搬移）。</summary>
    private static readonly string[] ShimNames = { "dsh", "dsh.cmd", "dsh.ps1" };

    /// <summary>安装根下的可搬移条目：node_modules 与转发脚本。</summary>
    private static readonly string[] RootEntries = { "node_modules", "dsh", "dsh.cmd", "dsh.ps1" };

    private const string StagingPrefix = ".staging-";
    private const string BackupPrefix = ".backup-";

    // ── 版本解析 ────────────────────────────────────────────

    /// <summary>
    /// 判断是否为可精确安装的版本号（而非 <c>latest</c> / <c>next</c> 之类的标签）。
    /// </summary>
    public static bool IsExactVersion(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }
        var s = spec.Trim();
        return char.IsDigit(s[0]) && s.Contains('.');
    }

    /// <summary>
    /// 从某源的 dist-tags 解析"应当安装的确切版本"：取 <c>latest</c> 与 <c>next</c> 中较新者。
    /// 全部缺失返回 null。
    /// </summary>
    public static string? ResolveVersion(DshRegistry registry)
    {
        string? best = null;
        foreach (var tag in new[] { "latest", "next" })
        {
            if (registry.DistTags.ValueKind == JsonValueKind.Object
                && registry.DistTags.TryGetProperty(tag, out var node)
                && node.ValueKind == JsonValueKind.String)
            {
                var candidate = node.GetString();
                if (!IsExactVersion(candidate))
                {
                    continue;
                }
                best = best is null || DshUpdater.CompareVersions(candidate!, best) > 0
                    ? candidate
                    : best;
            }
        }
        return best;
    }

    /// <summary>
    /// 为"首次自动安装"解析目标：选延迟最低的源 → 取该源最新的确切版本。
    /// 无可用源或版本时返回 null。
    /// </summary>
    public static async Task<(DshRegistry Registry, string Version)?> ResolveInstallTargetAsync(
        CancellationToken ct = default)
    {
        var registry = await DshUpdater.SelectBestRegistryAsync(ct);
        if (registry is null)
        {
            return null;
        }
        var version = ResolveVersion(registry);
        return version is null ? null : (registry, version);
    }

    // ── 安装主流程 ──────────────────────────────────────────

    /// <summary>
    /// 把 dsh 安装 / 升级到安装根目录（<see cref="DshPaths.InstallRoot"/>）。
    /// <paramref name="version"/> 必须是确切版本号（如 <c>0.1.5-rc.2</c>），不接受 <c>latest</c> 标签。
    /// </summary>
    public static async Task<DshUpgradeResult> InstallAsync(
        string version, string registryUrl, IProgress<string>? progress, CancellationToken ct)
    {
        if (!IsExactVersion(version))
        {
            return new DshUpgradeResult(false, false,
                $"内部错误：安装 dsh 必须指定确切版本号，收到 \"{version}\"。\n" +
                "直接使用 latest / next 这类标签会导致 dsh CLI 与插件包版本不一致，安装必然损坏。");
        }

        var node = DshPaths.BundledNode;
        var npmCli = DshPaths.BundledNpmCli;
        var installRoot = DshPaths.InstallRoot;
        if (!File.Exists(node) || !File.Exists(npmCli))
        {
            return new DshUpgradeResult(false, false,
                "运行时缺少 node/npm（安装包不完整），无法安装或升级 dsh。");
        }

        var stagingRoot = Path.Combine(installRoot, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(installRoot);
            Directory.CreateDirectory(stagingRoot);
            progress?.Report($"[准备] 暂存目录中完整安装 dsh@{version}（避免就地更新的残留文件）");

            var install = await RunNpmInstallAsync(
                node, npmCli, stagingRoot, version, registryUrl, progress, ct);
            if (install.Cancelled)
            {
                return install;
            }
            if (!install.Success)
            {
                return new DshUpgradeResult(false, false,
                    "npm 安装失败：\n" + Tail(install.Output, ErrorTailLines));
            }

            // 校验：必须与期望版本一致，且不存在比 CLI 更新的插件包（即版本偏斜）
            if (!VerifyTree(stagingRoot, version, out var problem))
            {
                return new DshUpgradeResult(false, false,
                    $"安装结果校验未通过，已保留原有安装：\n{problem}\n\n" +
                    Tail(install.Output, ErrorTailLines));
            }

            progress?.Report("[切换] 校验通过，正在替换原有安装…");
            if (!CommitStagedInstall(installRoot, stagingRoot, out var swapProblem))
            {
                return new DshUpgradeResult(false, false,
                    $"替换安装目录失败，原有安装已保留：\n{swapProblem}");
            }

            progress?.Report($"[完成] dsh 已更新到 {version}");
            return new DshUpgradeResult(true, false, $"dsh 已更新到 {version}。");
        }
        catch (OperationCanceledException)
        {
            return new DshUpgradeResult(false, true, "安装已取消。");
        }
        catch (Exception ex)
        {
            return new DshUpgradeResult(false, false, ex.Message);
        }
        finally
        {
            TryDelete(stagingRoot);
        }
    }

    /// <summary>用捆绑 npm 在指定 prefix 下安装 dsh 的确切版本，实时回调输出行。</summary>
    private static async Task<DshUpgradeResult> RunNpmInstallAsync(
        string node, string npmCli, string prefixRoot,
        string version, string registryUrl, IProgress<string>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = node,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = prefixRoot,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(npmCli);
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add("-g");
        psi.ArgumentList.Add("--prefix");
        psi.ArgumentList.Add(prefixRoot);
        psi.ArgumentList.Add("--registry");
        psi.ArgumentList.Add(registryUrl);
        psi.ArgumentList.Add("--omit=dev");
        psi.ArgumentList.Add("--no-audit");
        psi.ArgumentList.Add("--no-fund");
        // dsh 的部分依赖需要执行安装脚本（原生模块），这里显式放行
        psi.ArgumentList.Add("--allow-scripts=@deepseek-ai/dsh-subprocess-local,koffi,node-pty,@google/genai,protobufjs");
        // 传确切版本，绝不用 latest/next 标签（见类注释）
        psi.ArgumentList.Add($"{PackageName}@{version}");

        // 15 分钟超时兜底：网络卡死时自动终止，避免无限挂起
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(15));
        var token = timeoutCts.Token;

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return new DshUpgradeResult(false, false, "无法启动 npm 安装进程。");
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
                        if (output.Length > 0)
                        {
                            output.Append('\n');
                        }
                        output.Append(line);
                    }
                    progress?.Report(line);
                }
            }
            catch (OperationCanceledException)
            {
                // 取消 / 超时：读流被中断，交给下方退出分支统一处理
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
                if (proc.HasExited)
                {
                    tcs.TrySetResult(proc.ExitCode);
                }
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
                ct.IsCancellationRequested ? "安装已取消。" : "安装超时（15 分钟），已中止。");
        }
        return new DshUpgradeResult(exitCode == 0, false, text);
    }

    /// <summary>
    /// 体检"自动安装目录"中的 dsh：未安装视为健康（由上层走安装流程）；
    /// 已安装则要求版本自洽，否则说明是"CLI 旧、插件新"的偏斜安装，需要重装修复。
    /// </summary>
    public static bool IsManagedInstallHealthy(out string problem)
    {
        problem = string.Empty;
        if (!DshPaths.IsDshInstalled)
        {
            return true;
        }
        var cliVersion = ReadVersion(DshPaths.DshPackageJson);
        if (cliVersion is null)
        {
            problem = "无法读取已安装 dsh 的版本号，安装文件可能已损坏。";
            return false;
        }
        return VerifyTree(DshPaths.InstallRoot, cliVersion, out problem);
    }

    // ── 安装结果校验 ────────────────────────────────────────

    /// <summary>
    /// 校验某个 prefix 下的安装是否完整且版本自洽：
    /// <list type="number">
    /// <item>入口 <c>lib/bin.js</c> 存在；</item>
    /// <item>CLI 版本等于期望版本；</item>
    /// <item>没有任何 <c>dsh-*</c> 插件包版本高于 CLI（即不存在版本偏斜）。</item>
    /// </list>
    /// </summary>
    public static bool VerifyTree(string prefixRoot, string expectedVersion, out string problem)
    {
        problem = string.Empty;

        var dshDir = Path.Combine(prefixRoot, "node_modules", "@deepseek-ai", "dsh");
        var binScript = Path.Combine(dshDir, "lib", "bin.js");
        if (!File.Exists(binScript))
        {
            problem = "未找到 dsh 入口文件 node_modules/@deepseek-ai/dsh/lib/bin.js，安装不完整。";
            return false;
        }

        var cliVersion = ReadVersion(Path.Combine(dshDir, "package.json"));
        if (cliVersion is null)
        {
            problem = "无法读取 dsh 的 package.json 版本号。";
            return false;
        }
        if (DshUpdater.CompareVersions(cliVersion, expectedVersion) != 0)
        {
            problem = $"dsh CLI 实际版本为 {cliVersion}，与期望的 {expectedVersion} 不一致。";
            return false;
        }

        // 版本偏斜检测：插件包比 CLI 新，就是"CLI 没换掉、插件却被升级"的坏状态
        var skew = EnumerateDshPluginPackages(prefixRoot)
            .Select(ReadVersion)
            .Where(v => v is not null)
            .FirstOrDefault(v => DshUpdater.CompareVersions(v!, cliVersion) > 0);
        if (skew is not null)
        {
            problem = $"检测到版本偏斜：dsh CLI 为 {cliVersion}，但插件包已是 {skew}。" +
                      "这会导致运行时插件加载失败。";
            return false;
        }

        return true;
    }

    /// <summary>枚举安装树中所有 <c>@deepseek-ai/dsh-*</c> 插件包的 package.json 路径。</summary>
    private static IEnumerable<string> EnumerateDshPluginPackages(string prefixRoot)
    {
        // npm 全局安装会把依赖嵌套在包自身的 node_modules 下，两处都要看
        var scopes = new[]
        {
            Path.Combine(prefixRoot, "node_modules", "@deepseek-ai"),
            Path.Combine(prefixRoot, "node_modules", "@deepseek-ai", "dsh", "node_modules", "@deepseek-ai"),
        };
        foreach (var scope in scopes)
        {
            if (!Directory.Exists(scope))
            {
                continue;
            }
            foreach (var dir in Directory.EnumerateDirectories(scope))
            {
                if (Path.GetFileName(dir).StartsWith("dsh-", StringComparison.OrdinalIgnoreCase))
                {
                    yield return Path.Combine(dir, "package.json");
                }
            }
        }
    }

    private static string? ReadVersion(string packageJsonPath)
    {
        try
        {
            if (!File.Exists(packageJsonPath))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    // ── 暂存结果整体替换 ────────────────────────────────────

    /// <summary>
    /// 把暂存目录中的 node_modules 与转发脚本整体替换到安装根目录：
    /// 旧内容先移入备份目录，替换成功再删除备份；中途失败则回滚，绝不留半新半旧的状态。
    /// </summary>
    private static bool CommitStagedInstall(string installRoot, string stagingRoot, out string problem)
    {
        problem = string.Empty;

        var stagedModules = Path.Combine(stagingRoot, "node_modules");
        if (!Directory.Exists(stagedModules))
        {
            problem = "暂存目录中没有生成 node_modules，安装未完成。";
            return false;
        }

        var backupRoot = Path.Combine(installRoot, BackupPrefix + Guid.NewGuid().ToString("N"));
        var backedUp = new List<(string Live, string Backup)>();
        var installed = new List<string>();

        try
        {
            Directory.CreateDirectory(backupRoot);

            // 1) 旧内容挪到备份
            foreach (var name in RootEntries)
            {
                var live = Path.Combine(installRoot, name);
                if (!Directory.Exists(live) && !File.Exists(live))
                {
                    continue;
                }
                var dest = Path.Combine(backupRoot, name);
                MoveWithRetry(live, dest);
                backedUp.Add((live, dest));
            }

            // 2) 新内容搬到安装根
            foreach (var name in RootEntries)
            {
                var src = Path.Combine(stagingRoot, name);
                if (!Directory.Exists(src) && !File.Exists(src))
                {
                    continue;
                }
                var dest = Path.Combine(installRoot, name);
                MoveWithRetry(src, dest);
                installed.Add(dest);
            }
        }
        catch (Exception ex)
        {
            // 回滚：撤掉刚搬入的新内容，再把备份搬回原位
            foreach (var path in installed)
            {
                TryDelete(path);
            }
            foreach (var (live, backup) in backedUp)
            {
                try
                {
                    MoveWithRetry(backup, live);
                }
                catch
                {
                    // 回滚失败也无力回天，尽量继续
                }
            }
            TryDelete(backupRoot);
            problem = ex.Message;
            return false;
        }

        // 备份已无用处；即使删不掉（句柄未释放）也不影响使用
        TryDelete(backupRoot);
        return true;
    }

    /// <summary>同卷内的移动（目录用原子重命名）；Windows 上句柄释放有延迟，遇占用则重试。</summary>
    private static void MoveWithRetry(string from, string to)
    {
        const int maxAttempts = 8;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(from))
                {
                    Directory.Move(from, to);
                }
                else
                {
                    File.Move(from, to, overwrite: true);
                }
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(250 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(250 * attempt);
            }
        }
    }

    /// <summary>清理安装根下遗留的暂存 / 备份目录（上次异常退出可能残留）。</summary>
    public static void CleanupLeftovers()
    {
        try
        {
            var root = DshPaths.InstallRoot;
            if (!Directory.Exists(root))
            {
                return;
            }
            foreach (var dir in Directory.EnumerateDirectories(root, ".*"))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(StagingPrefix, StringComparison.Ordinal)
                    || name.StartsWith(BackupPrefix, StringComparison.Ordinal))
                {
                    TryDelete(dir);
                }
            }
        }
        catch
        {
            // 清理失败不影响功能
        }
    }

    // ── 小工具 ──────────────────────────────────────────────

    private static string Tail(string text, int lines)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "（无输出）";
        }
        var all = text.Split('\n');
        return all.Length <= lines ? text : string.Join("\n", all.TakeLast(lines));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删除失败（句柄未释放等）不影响功能，留给下次启动清理
        }
    }
}

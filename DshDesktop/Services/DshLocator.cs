using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DshDesktop.Services;

/// <summary>定位到的 dsh 运行方式：要么 <see cref="NodePath"/> + <see cref="ScriptPath"/>（node 跑 CLI 脚本），要么仅 <see cref="NodePath"/>（独立 exe）。</summary>
public sealed record DshRuntime(string NodePath, string ScriptPath)
{
    public string DisplayName => string.IsNullOrEmpty(ScriptPath)
        ? NodePath
        : $"{NodePath} {ScriptPath}";
}

/// <summary>
/// 按优先级查找 dsh 可执行位置：
/// 用户设置的路径 → PATH 中的 dsh 命令 → npm 全局安装位置。
/// 对 <c>dsh.cmd</c> shim 会解析其内容以得到真实的 <c>bin.js</c> 路径，从而用
/// <c>node</c> 直接运行，保证标准输出可被重定向解析。
/// </summary>
public static class DshLocator
{
    private static readonly Regex NodeInvocationPattern =
        new(@"(?i)(node(?:\.exe)?)\s+""?([^""\r\n\s]+\.js)""?", RegexOptions.Compiled);

    /// <summary>
    /// 定位自动安装目录中已安装的 dsh（安装根/DeepSeek Harness/node_modules/@deepseek-ai/dsh），
    /// 用捆绑 node（或系统 node 兜底）运行其 bin.js。
    /// </summary>
    private static DshRuntime? ResolveManaged()
    {
        try
        {
            if (DshPaths.IsDshInstalled)
            {
                var node = File.Exists(DshPaths.BundledNode)
                    ? DshPaths.BundledNode
                    : FindNode();
                if (!string.IsNullOrEmpty(node))
                {
                    return new DshRuntime(node, DshPaths.DshBinScript);
                }
            }
        }
        catch
        {
            // 安装目录异常时回退到其他定位方式
        }
        return null;
    }

    public static async Task<DshRuntime?> FindAsync(string? configuredPath)
    {
        // 0. 自动安装目录中已安装的 dsh（DshPaths.IsDshInstalled；无则返回 null）
        var managed = ResolveManaged();
        if (managed is not null)
        {
            return managed;
        }

        // 1. 用户在设置中指定的路径
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var rt = ResolveCandidate(configuredPath.Trim());
            if (rt is not null)
            {
                return rt;
            }
        }

        var nodePath = FindNode();

        // 2. PATH 中的 dsh
        var dshOnPath = FindOnPath("dsh");
        if (dshOnPath is not null)
        {
            var rt = ResolveCandidate(dshOnPath, nodePath);
            if (rt is not null)
            {
                return rt;
            }
        }

        // 3. npm 全局安装的常见位置
        if (nodePath is not null)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(appData, "npm", "@deepseek-ai", "dsh", "lib", "bin.js"),
                Path.Combine(appData, "npm", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
                Path.Combine(programFiles, "nodejs", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
                Path.Combine(localAppData, "Programs", "nodejs", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
            };
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return new DshRuntime(nodePath, candidate);
                }
            }
        }
        return await Task.FromResult<DshRuntime?>(null);
    }

    private static string? FindNode()
    {
        var onPath = FindOnPath("node");
        if (onPath is not null)
        {
            return onPath;
        }

        // 兜底：npm 常见安装位置下的 node.exe
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(programFiles, "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string? FindOnPath(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("Path") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }
            foreach (var ext in new[] { ".exe", ".cmd", ".bat", string.Empty })
            {
                var full = Path.Combine(dir.Trim(), name + ext);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }
        return null;
    }

    private static DshRuntime? ResolveCandidate(string path, string? fallbackNode = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var lower = path.ToLowerInvariant();

        if (lower.EndsWith(".js", StringComparison.Ordinal))
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var node = fallbackNode ?? FindNode();
            return node is null ? null : new DshRuntime(node, path);
        }

        if (lower.EndsWith(".cmd", StringComparison.Ordinal) || lower.EndsWith(".bat", StringComparison.Ordinal))
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var node = fallbackNode ?? FindNode();
            if (node is null)
            {
                return null;
            }
            try
            {
                // npm 生成的 dsh.cmd 形如：@"%~dp0\node.exe" "%~dp0\node_modules\@deepseek-ai\dsh\lib\bin.js" %*
                var content = File.ReadAllText(path);
                var match = NodeInvocationPattern.Match(content);
                if (match.Success)
                {
                    var js = match.Groups[2].Value;
                    if (!Path.IsPathRooted(js))
                    {
                        js = Path.GetFullPath(js, Path.GetDirectoryName(path)!);
                    }
                    if (File.Exists(js))
                    {
                        return new DshRuntime(node, js);
                    }
                }
            }
            catch
            {
                // shim 不可读则退化到直接运行（可能失败，交给用户处理）
            }
            return new DshRuntime(node, path);
        }

        if (lower.EndsWith(".exe", StringComparison.Ordinal) && File.Exists(path))
        {
            return new DshRuntime(path, string.Empty);
        }

        return null;
    }
}

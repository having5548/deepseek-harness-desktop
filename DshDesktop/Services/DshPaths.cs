using System;
using System.IO;

namespace DshDesktop.Services;

/// <summary>
/// 应用路径常量。
/// 设计说明：安装包只捆绑 Node 运行时（runtime\node.exe + runtime\node_modules\npm），
/// <b>不再捆绑 dsh</b>。dsh 首次启动时用捆绑的 node+npm 联网安装到
/// <see cref="InstallRoot"/>（应用所在盘的 DeepSeek Harness 文件夹），装完自动被
/// <see cref="DshLocator"/> 定位并绑定，此后启动直接复用。
/// </summary>
public static class DshPaths
{
    /// <summary>应用安装目录（exe 所在目录）。</summary>
    public static string AppDir => AppContext.BaseDirectory;

    /// <summary>捆绑 Node 运行时目录（随安装包分发，不含 dsh）。</summary>
    public static string BundledRuntimeDir => Path.Combine(AppDir, "runtime");

    public static string BundledNode => Path.Combine(BundledRuntimeDir, "node.exe");

    public static string BundledNpmCli => Path.Combine(
        BundledRuntimeDir, "node_modules", "npm", "bin", "npm-cli.js");

    /// <summary>
    /// dsh 安装根目录：应用所在盘的 <c>DeepSeek Harness</c> 文件夹。
    /// 例：应用装在 <c>H:\DeepSeek Harness\</c> 则 dsh 装到 <c>H:\DeepSeek Harness</c>。
    /// </summary>
    public static string InstallRoot
    {
        get
        {
            var drive = Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\";
            return Path.Combine(drive, "DeepSeek Harness");
        }
    }

    /// <summary>npm 把 dsh 装到安装根目录下的 node_modules\@deepseek-ai\dsh。</summary>
    public static string DshPackageDir => Path.Combine(
        InstallRoot, "node_modules", "@deepseek-ai", "dsh");

    public static string DshBinScript => Path.Combine(DshPackageDir, "lib", "bin.js");

    public static string DshPackageJson => Path.Combine(DshPackageDir, "package.json");

    /// <summary>dsh 是否已安装到安装根目录（bin.js 存在即视为已安装）。</summary>
    public static bool IsDshInstalled => File.Exists(DshBinScript);

    /// <summary>
    /// dsh 的 home 目录：环境变量 <c>DSH_HOME</c> 优先，否则 <c>%USERPROFILE%\.dsh</c>
    /// （与 dsh 自身的 resolveDshHome 规则一致）。
    /// </summary>
    public static string DshHome
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }
    }

    /// <summary>
    /// <c>$DSH_HOME/profiles/node_modules</c> —— dsh 的"模块回退"缓存：它以符号链接的形式
    /// 把安装目录的依赖闭包暴露给各 profile，使 profile 能解析 in-box bundles
    /// （<c>@deepseek-ai/dsh-base</c>、<c>dsh-web-app</c> 及其 <c>dsh-client-ui-*</c> 依赖）。
    ///
    /// <para>它是<b>纯派生缓存</b>：删掉后 dsh 下次启动会依据当前安装重新生成。
    /// 反之，如果安装目录被替换而这些链接没被重建（悬空或缺失），profile 就会在启动时
    /// 报 <c>Cannot find package '@deepseek-ai/dsh-client-ui-...' imported from ...\.dsh\profiles\web\</c>。</para>
    /// </summary>
    public static string ProfileModuleFallbackDir =>
        Path.Combine(DshHome, "profiles", "node_modules");

    /// <summary>安装目录中被回退缓存镜像的包作用域目录（dsh 的依赖闭包都在这里）。</summary>
    public static string InstalledScopeDir => Path.Combine(
        DshPackageDir, "node_modules", "@deepseek-ai");

    /// <summary>捆绑运行时是否完整（node + npm 都可用）。</summary>
    public static bool IsBundledRuntimeComplete =>
        File.Exists(BundledNode) && File.Exists(BundledNpmCli);
}

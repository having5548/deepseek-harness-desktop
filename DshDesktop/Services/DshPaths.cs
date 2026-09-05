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

    /// <summary>捆绑运行时是否完整（node + npm 都可用）。</summary>
    public static bool IsBundledRuntimeComplete =>
        File.Exists(BundledNode) && File.Exists(BundledNpmCli);
}

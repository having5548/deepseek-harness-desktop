using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DshDesktop.Services;

/// <summary>服务因插件加载失败而崩溃的信息。</summary>
public sealed record CrashInfo(IReadOnlyList<string> PluginNames, string ErrorLog);

/// <summary>
/// 管理 dsh web 服务子进程：以 <c>--no-open --port 0</c> 启动，解析
/// <c>dsh web: http://127.0.0.1:&lt;port&gt;</c> 输出行得到真实 URL，
/// 检测插件加载失败导致的崩溃，并负责退出时终止整棵进程树。
/// </summary>
public sealed class DshHostProcess : IDisposable
{
    private static readonly Regex UrlPattern = new(@"dsh web: (http://127\.0\.0\.1:\d+)", RegexOptions.Compiled);
    private static readonly Regex PluginListPattern = new(@"plugin\(s\) failed to load:\s*(.+?)(?:;|$)", RegexOptions.Compiled);
    private static readonly Regex PluginEntryPattern = new(@"failed to apply loader entry\s+\S+\s+\(([^)]+)\)", RegexOptions.Compiled);

    private const int MaxErrorBuffer = 200;

    private readonly string _workingDirectory;
    private readonly object _bufferLock = new();
    private readonly List<string> _errorBuffer = new();
    private Process? _process;

    /// <summary>服务已就绪，参数为可访问的 Web UI URL。</summary>
    public event Action<string>? UrlReady;

    /// <summary>服务标准输出（每行）。</summary>
    public event Action<string>? Output;

    /// <summary>服务错误输出（每行）。</summary>
    public event Action<string>? Error;

    /// <summary>服务进程退出，参数为退出码。</summary>
    public event Action<int>? Exited;

    /// <summary>检测到插件加载失败导致的崩溃（参数含插件名与错误日志）。</summary>
    public event Action<CrashInfo>? Crashed;

    public DshHostProcess()
    {
        // 以用户主目录为工作目录，便于 dsh 读取用户级的 .env（如 DEEPSEEK_API_KEY）
        _workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public bool IsRunning
    {
        get
        {
            var proc = _process;
            try
            {
                return proc is not null && !proc.HasExited;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public void Start(DshRuntime runtime)
    {
        if (IsRunning)
        {
            return;
        }

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (string.IsNullOrEmpty(runtime.ScriptPath))
        {
            // 直接的可执行文件（dsh.exe 等）
            psi.FileName = runtime.NodePath;
        }
        else
        {
            // node <bin.js>
            psi.FileName = runtime.NodePath;
            psi.ArgumentList.Add(runtime.ScriptPath);
        }

        psi.ArgumentList.Add("--profile");
        psi.ArgumentList.Add("web");
        psi.ArgumentList.Add("--no-open");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add("0");

        // 注入 PATH：`dsh plugin` 内部 spawnSync('pnpm') 依赖 PATH 找到捆绑的 pnpm
        var runtimeDir = Path.GetDirectoryName(runtime.NodePath);
        var path = Environment.GetEnvironmentVariable("Path") ?? string.Empty;
        psi.Environment["Path"] = string.IsNullOrEmpty(runtimeDir) ? path : runtimeDir + ";" + path;

        lock (_bufferLock)
        {
            _errorBuffer.Clear();
        }

        _process = Process.Start(psi);
        if (_process is null)
        {
            throw new InvalidOperationException("无法启动 dsh 服务进程。");
        }

        _ = Task.Run(() => PumpAsync(_process.StandardOutput, isError: false));
        _ = Task.Run(() => PumpAsync(_process.StandardError, isError: true));
        _ = Task.Run(WaitForExitAsync);
    }

    private async Task PumpAsync(TextReader reader, bool isError)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (isError)
            {
                Error?.Invoke(line);
                lock (_bufferLock)
                {
                    _errorBuffer.Add(line);
                    if (_errorBuffer.Count > MaxErrorBuffer)
                    {
                        _errorBuffer.RemoveRange(0, _errorBuffer.Count - MaxErrorBuffer);
                    }
                }
            }
            else
            {
                Output?.Invoke(line);
                var match = UrlPattern.Match(line);
                if (match.Success)
                {
                    UrlReady?.Invoke(match.Groups[1].Value);
                }
            }
        }
    }

    private async Task WaitForExitAsync()
    {
        var proc = _process;
        if (proc is null)
        {
            return;
        }
        int code;
        try
        {
            code = await Task.Run(() =>
            {
                proc.WaitForExit();
                return proc.ExitCode;
            });
        }
        catch (ObjectDisposedException)
        {
            // Stop() 已终止并释放进程
            return;
        }
        Exited?.Invoke(code);

        var crash = TryDetectCrash();
        if (crash is not null)
        {
            Crashed?.Invoke(crash);
        }
    }

    /// <summary>从错误缓冲中识别"插件加载失败导致崩溃"的情况并提取插件名与日志。</summary>
    private CrashInfo? TryDetectCrash()
    {
        string[] lines;
        lock (_bufferLock)
        {
            lines = _errorBuffer.ToArray();
        }
        var text = string.Join("\n", lines);
        var names = new List<string>();

        var listMatch = PluginListPattern.Match(text);
        foreach (Match m in PluginEntryPattern.Matches(text))
        {
            AddName(names, m.Groups[1].Value);
        }
        if (listMatch.Success)
        {
            foreach (var part in listMatch.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                AddName(names, part);
            }
        }

        var isLoadFailure = listMatch.Success
            || text.Contains("plugin tree failed to load", StringComparison.Ordinal)
            || text.Contains("fatal load failure", StringComparison.Ordinal);
        if (!isLoadFailure || names.Count == 0)
        {
            return null;
        }

        var log = string.Join("\n", lines.TakeLast(60));
        return new CrashInfo(names, log);
    }

    private static void AddName(List<string> names, string raw)
    {
        // 去掉可能的 @scope/name@version 尾缀
        var name = raw.Trim();
        if (name.Length == 0 || names.Contains(name))
        {
            return;
        }
        names.Add(name);
    }

    /// <summary>终止服务进程及其整棵子进程树。结束后 <see cref="_process"/> 置空，允许干净地再次 <see cref="Start"/>。</summary>
    public void Stop()
    {
        var proc = _process;
        _process = null;
        if (proc is null)
        {
            return;
        }
        try
        {
            if (!proc.HasExited)
            {
                using var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList =
                    {
                        "/PID", proc.Id.ToString(),
                        "/T",   // 终止子进程树
                        "/F",   // 强制终止
                    },
                });
                killer?.WaitForExit(3000);
                proc.WaitForExit(2000);
            }
        }
        catch
        {
            // 进程可能已退出或已被释放
        }
        finally
        {
            try
            {
                proc.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

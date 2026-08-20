using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DshDesktop.Services;

/// <summary>
/// 管理 dsh web 服务子进程：以 <c>--no-open --port 0</c> 启动，解析
/// <c>dsh web: http://127.0.0.1:&lt;port&gt;</c> 输出行得到真实 URL，
/// 并负责退出时终止整棵进程树。
/// </summary>
public sealed class DshHostProcess : IDisposable
{
    private static readonly Regex UrlPattern = new(@"dsh web: (http://127\.0\.0\.1:\d+)", RegexOptions.Compiled);

    private readonly string _workingDirectory;
    private Process? _process;

    /// <summary>服务已就绪，参数为可访问的 Web UI URL。</summary>
    public event Action<string>? UrlReady;

    /// <summary>服务标准输出（每行）。</summary>
    public event Action<string>? Output;

    /// <summary>服务错误输出（每行）。</summary>
    public event Action<string>? Error;

    /// <summary>服务进程退出，参数为退出码。</summary>
    public event Action<int>? Exited;

    public DshHostProcess()
    {
        // 以用户主目录为工作目录，便于 dsh 读取用户级的 .env（如 DEEPSEEK_API_KEY）
        _workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public bool IsRunning => _process is { HasExited: false };

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
        if (_process is null)
        {
            return;
        }
        var code = await Task.Run(() =>
        {
            _process.WaitForExit();
            return _process.ExitCode;
        });
        Exited?.Invoke(code);
    }

    /// <summary>终止服务进程及其整棵子进程树。</summary>
    public void Stop()
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }
        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "/PID", _process.Id.ToString(),
                    "/T",   // 终止子进程树
                    "/F",   // 强制终止
                },
            });
            killer?.WaitForExit(3000);
        }
        catch
        {
            // 进程可能已退出
        }
        finally
        {
            try
            {
                _process.Dispose();
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

using System;
using System.IO;
using System.Threading.Tasks;
using DshDesktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace DshDesktop;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly DshHostProcess _host = new();

    private bool _initialized;
    private bool _navigatedToApp;
    private string? _currentUrl;

    public MainWindow()
    {
        InitializeComponent();
        Title = "DeepSeek Harness";
        _settings = AppSettings.Load();

        _host.UrlReady += OnUrlReady;
        _host.Error += OnHostError;
        _host.Exited += OnHostExited;

        Closed += OnWindowClosed;
        Activated += OnActivated;
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_initialized)
        {
            _initialized = true;
            await InitializeAsync();
        }
    }

    private async Task InitializeAsync()
    {
        ShowStatus("正在初始化…", "");
        try
        {
            await WebView.EnsureCoreWebView2Async();
            WireWebView2();

            var runtime = await DshLocator.FindAsync(_settings.DshPath);
            if (runtime is null)
            {
                ShowStatus(
                    "未检测到 DeepSeek Harness CLI",
                    "请先安装：npm install -g @deepseek-ai/dsh（或从仓库源码构建）。\n也可以点击右上角设置按钮手动指定 dsh 可执行文件路径。",
                    isError: true);
                return;
            }

            ServiceStateText.Text = "正在启动服务…";
            ShowStatus("正在启动 DeepSeek Harness 服务…", runtime.DisplayName);
            _host.Start(runtime);
        }
        catch (Exception ex)
        {
            ShowStatus("初始化失败", ex.Message, isError: true);
        }
    }

    private void WireWebView2()
    {
        WebView.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        WebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
        WebView.NavigateToString(WaitingHtml);
    }

    private void OnUrlReady(string url)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _currentUrl = url;
            ServiceStateText.Text = "服务运行中";
            if (!_navigatedToApp)
            {
                _navigatedToApp = true;
                HideStatus();
                WebView.Source = new Uri(url);
            }
        });
    }

    private void OnHostError(string line)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_navigatedToApp)
            {
                StatusDetail.Text = line;
            }
        });
    }

    private void OnHostExited(int code)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ServiceStateText.Text = $"服务已退出 ({code})";
            if (_navigatedToApp)
            {
                ShowStatus(
                    "DeepSeek Harness 服务已退出",
                    $"退出码 {code}。点击工具栏重新加载可重启服务。",
                    isError: true);
            }
        });
    }

    // ── 工具栏 ──────────────────────────────────────────────

    private void BackButton_Click(object sender, RoutedEventArgs e) => WebView.GoBack();
    private void ForwardButton_Click(object sender, RoutedEventArgs e) => WebView.GoForward();

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_navigatedToApp)
        {
            WebView.Reload();
        }
        else
        {
            RestartHostAsync();
        }
    }

    private void ExternalButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentUrl))
        {
            OpenExternal(_currentUrl);
        }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_settings.DshPath, this);
        dialog.XamlRoot = Content.XamlRoot;
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _settings.DshPath = dialog.DshPath;
            _settings.Save();
            await RestartHostAsync();
        }
    }

    // ── 生命周期 ────────────────────────────────────────────

    private async Task RestartHostAsync()
    {
        _host.Stop();
        _navigatedToApp = false;
        _currentUrl = null;
        var runtime = await DshLocator.FindAsync(_settings.DshPath);
        if (runtime is null)
        {
            ShowStatus(
                "未检测到 DeepSeek Harness CLI",
                "请检查设置中的 dsh 路径是否正确。",
                isError: true);
            return;
        }
        ServiceStateText.Text = "正在启动服务…";
        ShowStatus("正在启动 DeepSeek Harness 服务…", runtime.DisplayName);
        _host.Start(runtime);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _host.Dispose();
    }

    private static void OpenExternal(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 忽略打开失败
        }
    }

    // ── UI 辅助 ─────────────────────────────────────────────

    private void ShowStatus(string title, string detail, bool isError = false)
    {
        StatusOverlay.Visibility = Visibility.Visible;
        StatusRing.IsActive = !isError;
        StatusText.Text = title;
        StatusDetail.Text = detail;
    }

    private void HideStatus()
    {
        StatusOverlay.Visibility = Visibility.Collapsed;
        StatusRing.IsActive = false;
    }

    private const string WaitingHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
        "<style>html,body{height:100%;margin:0;background:#111827;color:#e5e7eb;" +
        "font-family:'Segoe UI',system-ui,sans-serif;display:flex;align-items:center;justify-content:center;}" +
        ".card{text-align:center;padding:32px;}.spin{width:44px;height:44px;margin:0 auto 20px;border:4px solid #374151;" +
        "border-top-color:#3b82f6;border-radius:50%;animation:r 1s linear infinite;}" +
        "@keyframes r{to{transform:rotate(360deg)}}h1{font-size:20px;font-weight:600;margin:0 0 8px;}" +
        "p{color:#9ca3af;margin:0;}</style></head><body>" +
        "<div class=\"card\"><div class=\"spin\"></div><h1>DeepSeek Harness</h1><p>正在启动本地服务…</p></div>" +
        "</body></html>";
}

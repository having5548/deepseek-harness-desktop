using System;
using System.Collections.Generic;
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
    private bool _handlingCrash;
    private string? _currentUrl;

    public MainWindow()
    {
        InitializeComponent();
        Title = "DeepSeek Harness";
        _settings = AppSettings.Load();

        _host.UrlReady += OnUrlReady;
        _host.Error += OnHostError;
        _host.Exited += OnHostExited;
        _host.Crashed += OnCrash;

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

    private async void PluginsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PluginDialog(this, _settings);
        dialog.XamlRoot = Content.XamlRoot;
        dialog.PluginsChanged += async () =>
        {
            ServiceStateText.Text = "插件已变更，正在重启服务…";
            await RestartHostAsync();
        };
        await dialog.ShowAsync();
    }

    // ── 崩溃处理：自动屏蔽报错插件 + 重启 + 弹窗 ───────────

    private void OnCrash(CrashInfo crash)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_handlingCrash)
            {
                return;
            }
            _handlingCrash = true;
            try
            {
                // 1. 自动屏蔽（卸载 + 记录）报错插件
                var disabled = new List<string>();
                foreach (var pkg in crash.PluginNames)
                {
                    var ok = await PluginManager.DisablePluginAsync(_settings, pkg);
                    if (ok)
                    {
                        disabled.Add(pkg);
                    }
                }

                // 2. 以安全配置自动重启（已排除报错插件）
                await RestartHostAsync();

                // 3. 弹窗告知用户（插件名 + 报错日志 + 一键重启/恢复）
                await ShowCrashDialogAsync(crash, disabled);
            }
            finally
            {
                _handlingCrash = false;
            }
        });
    }

    private async Task ShowCrashDialogAsync(CrashInfo crash, IReadOnlyList<string> disabled)
    {
        var panel = new StackPanel { Spacing = 10, Width = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = $"以下插件加载失败导致服务退出：{string.Join(", ", crash.PluginNames)}",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        if (disabled.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"已自动屏蔽并卸载：{string.Join(", ", disabled)}。服务已尝试以安全配置重启。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.85,
            });
        }
        panel.Children.Add(new TextBlock { Text = "报错日志（末尾部分）：", FontSize = 12, Opacity = 0.7 });
        panel.Children.Add(new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(6),
            Child = new ScrollViewer
            {
                MaxHeight = 240,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(crash.ErrorLog) ? "（无错误输出）" : crash.ErrorLog,
                    FontSize = 11,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        });

        var dialog = new ContentDialog
        {
            Title = "插件加载失败（已自动屏蔽）",
            Content = panel,
            PrimaryButtonText = "一键重启",
            SecondaryButtonText = disabled.Count > 0 ? "恢复该插件" : null,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await RestartHostAsync();
        }
        else if (result == ContentDialogResult.Secondary && disabled.Count > 0)
        {
            foreach (var pkg in disabled)
            {
                await PluginManager.EnablePluginAsync(_settings, pkg);
            }
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DshDesktop.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using WinRT.Interop;

namespace DshDesktop;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly DshHostProcess _host = new();

    private bool _initialized;
    private bool _navigatedToApp;
    private bool _handlingCrash;
    private string? _currentUrl;
    private CancellationTokenSource? _startTimeoutCts;
    private bool _installingDsh;
    private bool _updatingDsh;
    private bool _titleBarReady;
    private readonly List<string> _startupLog = new();
    private const int MaxStartupLogLines = 300;

    public MainWindow()
    {
        InitializeComponent();
        Title = "DeepSeek Harness";
        _settings = AppSettings.Load();

        _host.UrlReady += OnUrlReady;
        _host.Output += OnHostOutput;
        _host.Error += OnHostError;
        _host.Exited += OnHostExited;
        _host.Crashed += OnCrash;

        Closed += OnWindowClosed;
        Activated += OnActivated;
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        SetupAppTitleBar();
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
            await RunStartupAsync();
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
            _startTimeoutCts?.Cancel();
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

    private void OnHostOutput(string line)
    {
        DispatcherQueue.TryEnqueue(() => AppendStartupLog(line));
    }

    private void OnHostError(string line)
    {
        DispatcherQueue.TryEnqueue(() => AppendStartupLog(line));
    }

    private void OnHostExited(int code)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _startTimeoutCts?.Cancel();
            ServiceStateText.Text = $"服务已退出 ({code})";
            if (!_navigatedToApp)
            {
                // 启动阶段即退出：立刻给出失败提示并保留日志，方便定位原因
                ShowStatus(
                    "服务启动失败",
                    $"dsh 进程启动后随即退出（退出码 {code}）。\n请查看下方启动日志定位原因，可点击工具栏「重新加载」重试，或在设置中指定其他 dsh 路径。",
                    isError: true);
                StartupLogBorder.Visibility = Visibility.Visible;
            }
            else
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
        var dialog = new SettingsDialog(
            _settings.DshPath,
            DshUpdater.GetLocalVersion(),
            this);
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

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckForDshUpdateAsync();
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

    // ── dsh 安装与更新 ─────────────────────────────────────

    /// <summary>
    /// 手动检查 dsh 新版本（工具栏按钮）：有新版 → 询问升级；无新版 → 提示已是最新；无网络 → 失败提示。
    /// 启动时不再自动检查版本（仅在 dsh 缺失时自动安装），见 <see cref="AutoInstallDshIfNeededAsync"/>。
    /// </summary>
    private async Task CheckForDshUpdateAsync()
    {
        if (_updatingDsh)
        {
            return;
        }

        // 尚未安装 dsh：手动检查无意义，改为引导自动安装
        if (DshUpdater.GetLocalVersion() is null)
        {
            var install = new ContentDialog
            {
                Title = "尚未安装 dsh",
                Content = new TextBlock
                {
                    Text = $"检测到本机尚未安装 DeepSeek Harness (dsh)。\n是否立即联网安装最新版到：\n{DshPaths.InstallRoot}",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420,
                },
                PrimaryButtonText = "立即安装",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            if (await install.ShowAsync() == ContentDialogResult.Primary)
            {
                await RestartHostAsync(); // 内部：找不到 dsh → 自动安装
            }
            return;
        }

        DshUpdateInfo info;
        try
        {
            info = await DshUpdater.CheckForUpdateAsync();
        }
        catch
        {
            await ShowMessageDialogAsync("检查更新失败", "无法连接任何 npm 源，请检查网络后重试。");
            return;
        }

        // 所有源均不可达（未选到任何 registry）
        if (info.RegistryName is null)
        {
            await ShowMessageDialogAsync("检查更新失败", "无法连接任何 npm 源（官方与国内镜像均不可达），请检查网络后重试。");
            return;
        }

        if (!info.IsUpdateAvailable)
        {
            var src = info.LatencyMs is { } ms ? $"（{info.RegistryName}，{ms}ms）" : $"（{info.RegistryName}）";
            await ShowMessageDialogAsync("已是最新版本", $"当前 dsh 版本：{info.LocalVersion}\n更新源：{src}");
            return;
        }

        var result = await ShowUpdateDialogAsync(info);
        if (result == ContentDialogResult.Primary && !string.IsNullOrEmpty(info.LatestVersion))
        {
            await UpgradeDshAsync(info.LatestVersion, info.RegistryName, info.RegistryUrl);
        }
    }

    private async Task<ContentDialogResult> ShowUpdateDialogAsync(DshUpdateInfo info)
    {
        var panel = new StackPanel { Spacing = 10, Width = 440 };
        panel.Children.Add(new TextBlock
        {
            Text = $"检测到 DeepSeek Harness (dsh) 新版本：\n{info.LocalVersion} → {info.LatestVersion}",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        var src = info.LatencyMs is { } ms ? $"（{info.RegistryName}，{ms}ms）" : $"（{info.RegistryName}）";
        panel.Children.Add(new TextBlock
        {
            Text = $"升级需要联网下载，完成后会自动重启服务。是否现在升级？\n更新源：{src}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.85,
        });

        var dialog = new ContentDialog
        {
            Title = "发现新版本",
            Content = panel,
            PrimaryButtonText = "立即升级",
            CloseButtonText = "稍后再说",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        return await dialog.ShowAsync();
    }

    private async Task UpgradeDshAsync(string version, string? registryName, string? registryUrl)
    {
        if (_updatingDsh)
        {
            return;
        }
        _updatingDsh = true;
        var cts = new CancellationTokenSource();
        try
        {
            var statusText = new TextBlock
            {
                Text = "准备升级…",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                MaxHeight = 200,
            };
            var panel = new StackPanel { Spacing = 12, Width = 460 };
            panel.Children.Add(new TextBlock
            {
                Text = $"{DshUpdater.GetLocalVersion() ?? "?"} → {version}" +
                       (registryName is null ? "" : $"\n更新源：{registryName}"),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            panel.Children.Add(new ProgressRing
            {
                IsActive = true,
                Width = 32,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            panel.Children.Add(statusText);

            var dialog = new ContentDialog
            {
                Title = "正在升级 dsh…",
                Content = panel,
                CloseButtonText = "取消",
                XamlRoot = Content.XamlRoot,
            };
            var upgradeFinished = false;
            dialog.Closed += (_, _) =>
            {
                if (!upgradeFinished)
                {
                    cts.Cancel();
                }
            };
            _ = dialog.ShowAsync();

            var progress = new Progress<string>(line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    statusText.Text = line;
                }
            });

            // 停止服务，避免升级过程中占用运行文件
            _host.Stop();
            _navigatedToApp = false;
            _currentUrl = null;

            var result = await DshUpdater.UpgradeAsync(
                version,
                registryUrl ?? "https://registry.npmjs.org/",
                progress,
                cts.Token);

            upgradeFinished = true;
            try
            {
                dialog.Hide();
            }
            catch
            {
                // 弹窗可能已被用户关闭
            }

            if (result.Cancelled)
            {
                await ShowMessageDialogAsync("升级已取消", result.Output);
                await RestartHostAsync();
                return;
            }

            if (!result.Success)
            {
                await ShowMessageDialogAsync("dsh 升级失败", string.IsNullOrWhiteSpace(result.Output) ? "未知错误" : result.Output);
                await RestartHostAsync();
                return;
            }

            await RestartHostAsync();
            await ShowMessageDialogAsync("升级完成", $"dsh 已成功升级到 {version}。");
        }
        finally
        {
            _updatingDsh = false;
            cts.Dispose();
        }
    }

    private async Task ShowMessageDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                MaxHeight = 360,
            },
            CloseButtonText = "确定",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    // ── 生命周期 ────────────────────────────────────────────

    /// <summary>
    /// 统一的启动入口：定位 dsh（必要时自动联网安装最新版），然后启动服务。
    /// 首次运行 / dsh 缺失时自动安装，安装过程实时写入启动日志，成功后即可被
    /// <see cref="DshLocator"/> 自动定位（即"自动绑定安装目录"）；失败则阻止启动并提示重试。
    /// </summary>
    private async Task RunStartupAsync()
    {
        if (_installingDsh)
        {
            return;
        }
        try
        {
            var runtime = await DshLocator.FindAsync(_settings.DshPath);
            if (runtime is null)
            {
                // 用户手动指定了路径但找不到 → 直接报错，不自动覆盖用户意图
                if (!string.IsNullOrWhiteSpace(_settings.DshPath))
                {
                    ShowStatus(
                        "未检测到 DeepSeek Harness CLI",
                        $"设置中指定的 dsh 路径不可用：{_settings.DshPath}\n请点击右上角设置修正路径，或清空后使用自动安装。",
                        isError: true);
                    return;
                }

                // 未指定路径且本机没有 dsh → 自动联网安装最新版
                if (!await AutoInstallDshIfNeededAsync())
                {
                    return; // 失败提示已在安装方法中给出，等待用户重试
                }
                runtime = await DshLocator.FindAsync(_settings.DshPath);
                if (runtime is null)
                {
                    ShowStatus(
                        "自动安装失败",
                        "dsh 已安装但无法定位，请点击工具栏「重新加载」重试，或检查安装目录权限。",
                        isError: true);
                    return;
                }
            }

            ServiceStateText.Text = "正在启动服务…";
            ShowStatus("正在启动 DeepSeek Harness 服务…", runtime.DisplayName);
            BeginStartupLog();
            _host.Start(runtime);
            ArmStartTimeout();
        }
        catch (Exception ex)
        {
            ServiceStateText.Text = "服务启动失败";
            ShowStatus("服务启动失败", ex.Message, isError: true);
        }
    }

    /// <summary>
    /// 尝试自动安装 dsh（仅当未安装时）：选最快 npm 源 → 安装 <c>latest</c> 到
    /// <see cref="DshPaths.InstallRoot"/>，实时把 npm 输出写入黑底启动日志。
    /// 返回是否已成功安装（未安装或失败返回 false）。
    /// </summary>
    private async Task<bool> AutoInstallDshIfNeededAsync()
    {
        if (_installingDsh)
        {
            return false;
        }
        _installingDsh = true;
        try
        {
            if (!DshPaths.IsBundledRuntimeComplete)
            {
                ShowStatus(
                    "自动安装不可用",
                    "安装包缺少捆绑的 Node/npm 运行时，无法自动安装 dsh。\n请重新安装本应用，或在设置中手动指定 dsh 路径。",
                    isError: true);
                return false;
            }

            ServiceStateText.Text = "正在安装 dsh…";
            ShowStatus(
                "首次使用：正在自动安装 DeepSeek Harness (dsh)…",
                $"安装目录：{DshPaths.InstallRoot}\n联网下载可能需要几分钟，进度见下方日志。");
            BeginStartupLog();
            AppendStartupLog($"[dsh] 未检测到 dsh，开始自动安装到 {DshPaths.InstallRoot}");
            AppendStartupLog("[dsh] 正在探测最快的 npm 源（官方 + 国内镜像）…");

            DshRegistry? registry;
            try
            {
                registry = await DshUpdater.SelectBestRegistryAsync();
            }
            catch
            {
                registry = null;
            }
            if (registry is null)
            {
                ShowStatus(
                    "自动安装失败",
                    "无法连接任何 npm 源（官方与国内镜像均不可达）。\n请检查网络后点击工具栏「重新加载」重试，或在设置中手动指定 dsh。",
                    isError: true);
                return false;
            }
            AppendStartupLog($"[dsh] 已选择更新源：{registry.Name}（{registry.LatencyMs}ms）");
            AppendStartupLog("[dsh] 开始安装 @deepseek-ai/dsh@latest …");

            using var cts = new CancellationTokenSource();
            var progress = new Progress<string>(line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    DispatcherQueue.TryEnqueue(() => AppendStartupLog(line));
                }
            });

            var result = await DshUpdater.UpgradeAsync("latest", registry.Url, progress, cts.Token);

            if (result.Cancelled)
            {
                ShowStatus(
                    "自动安装已取消",
                    "可在网络就绪后点击工具栏「重新加载」重试。",
                    isError: true);
                return false;
            }
            if (!result.Success)
            {
                AppendStartupLog(string.IsNullOrWhiteSpace(result.Output) ? "[dsh] 安装失败" : result.Output);
                ShowStatus(
                    "自动安装失败",
                    "npm 安装未成功，详情见下方日志。\n可点击工具栏「重新加载」重试，或在设置中手动指定 dsh 路径。",
                    isError: true);
                return false;
            }

            AppendStartupLog($"[dsh] 安装完成：{DshUpdater.GetLocalVersion() ?? "已安装"}");
            return true;
        }
        catch (Exception ex)
        {
            ShowStatus("自动安装失败", ex.Message, isError: true);
            return false;
        }
        finally
        {
            _installingDsh = false;
        }
    }

    private async Task RestartHostAsync()
    {
        try
        {
            _host.Stop();
            _navigatedToApp = false;
            _currentUrl = null;
            await RunStartupAsync();
        }
        catch (Exception ex)
        {
            ServiceStateText.Text = "服务启动失败";
            ShowStatus("服务启动失败", ex.Message, isError: true);
        }
    }

    /// <summary>启动 45 秒超时：若服务未就绪（如插件加载挂起），提示用户而不是无限等待。</summary>
    private void ArmStartTimeout()
    {
        _startTimeoutCts?.Cancel();
        var cts = new CancellationTokenSource();
        _startTimeoutCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(45), cts.Token);
            }
            catch (TaskCanceledException)
            {
                return; // URL 已就绪
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_navigatedToApp)
                {
                    return;
                }
                ServiceStateText.Text = "服务启动超时";
                ShowStatus(
                    "服务启动超时",
                    "dsh 服务未在 45 秒内就绪，可能是插件不兼容导致加载挂起。\n可点击工具栏“插件”在“已屏蔽”中处理，或点击重新加载重试。",
                    isError: true);
            });
        });
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

    // ── 一体化标题栏 ──────────────────────────────────────

    /// <summary>把内容延伸到标题栏，并将顶部条设为可拖动区域（按钮仍可点击），获得一体化观感。</summary>
    private void SetupAppTitleBar()
    {
        if (_titleBarReady)
        {
            return;
        }
        _titleBarReady = true;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow is null)
            {
                return;
            }
            appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarHost);
        }
        catch
        {
            // 定制失败时回退到系统默认标题栏，不影响功能
        }
    }

    // ── 启动日志 ──────────────────────────────────────────

    /// <summary>新一次启动前清空日志并显示日志框。</summary>
    private void BeginStartupLog()
    {
        _startupLog.Clear();
        StartupLogText.Text = string.Empty;
        StartupLogBorder.Visibility = Visibility.Visible;
    }

    private void ClearStartupLog()
    {
        _startupLog.Clear();
        StartupLogText.Text = string.Empty;
        StartupLogBorder.Visibility = Visibility.Visible;
    }

    /// <summary>把一行服务输出追加到黑底日志框，自动滚到底部，保留最近 <see cref="MaxStartupLogLines"/> 行。</summary>
    private void AppendStartupLog(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }
        _startupLog.Add(line);
        if (_startupLog.Count > MaxStartupLogLines)
        {
            _startupLog.RemoveRange(0, _startupLog.Count - MaxStartupLogLines);
        }
        StartupLogText.Text = string.Join("\n", _startupLog);
        if (StartupLogBorder.Visibility != Visibility.Visible)
        {
            StartupLogBorder.Visibility = Visibility.Visible;
        }
        if (StartupLogScroller.ExtentHeight > 0)
        {
            StartupLogScroller.ChangeView(null, StartupLogScroller.ExtentHeight, null);
        }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        ClearStartupLog();
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

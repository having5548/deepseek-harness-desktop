using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DshDesktop.Services;

/// <summary>
/// 插件管理对话框：从 GitHub 的 <c>dsh-plugin</c> 主题列出可安装插件，
/// 一键安装（安装成功后通过 <see cref="PluginsChanged"/> 通知主窗口重启服务），
/// 并展示已屏蔽（加载失败被自动禁用）的插件供恢复。
/// </summary>
public sealed class PluginDialog : ContentDialog
{
    /// <summary>有插件被安装或恢复，主窗口应重启服务。</summary>
    public event Action? PluginsChanged;

    private readonly MainWindow _owner;
    private readonly AppSettings _settings;
    private readonly StackPanel _installedPanel = new() { Spacing = 8 };
    private readonly TextBlock _installedStatus = new() { FontSize = 12, Opacity = 0.7 };
    private readonly StackPanel _remotePanel = new() { Spacing = 8 };
    private readonly TextBlock _remoteStatus = new() { FontSize = 12, Opacity = 0.7 };
    private readonly StackPanel _disabledPanel = new() { Spacing = 8 };
    private readonly TextBlock _disabledStatus = new() { FontSize = 12, Opacity = 0.7 };
    private readonly Button _refreshButton = new() { Content = "刷新列表" };

    /// <summary>profile 模板内置、不应卸载的 bundle。</summary>
    private static readonly HashSet<string> BuiltinBundles = new(StringComparer.OrdinalIgnoreCase)
    {
        "@deepseek-ai/dsh-base",
        "@deepseek-ai/dsh-web-app",
        "@deepseek-ai/dsh-headless",
    };

    public PluginDialog(MainWindow owner, AppSettings settings)
    {
        _owner = owner;
        _settings = settings;
        Title = "插件管理";
        CloseButtonText = "关闭";
        DefaultButton = ContentDialogButton.Close;

        var scroll = new ScrollViewer
        {
            MaxHeight = 440,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        // 不强制固定宽度：让 ContentDialog 自适应，避免窄屏时右侧内容被裁剪
        var layout = new StackPanel { Spacing = 14, MinWidth = 400, MaxWidth = 520 };

        // ── 已安装插件区 ──────────────────────────
        layout.Children.Add(new TextBlock
        {
            Text = "已安装插件",
            FontWeight = FontWeights.SemiBold,
        });
        layout.Children.Add(_installedStatus);
        layout.Children.Add(new Border
        {
            Child = _installedPanel,
            Padding = new Thickness(2),
        });

        // ── 远程插件区 ────────────────────────────
        var remoteHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        remoteHeader.Children.Add(new TextBlock
        {
            Text = "从 DSH Market 获取",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _refreshButton.Click += async (_, _) => await LoadRemoteAsync();
        remoteHeader.Children.Add(_refreshButton);
        layout.Children.Add(remoteHeader);
        layout.Children.Add(_remoteStatus);
        layout.Children.Add(new TextBlock
        {
            Text = "安装后自动重启服务生效；若插件导致服务崩溃会被自动屏蔽。",
            FontSize = 11,
            Opacity = 0.6,
        });
        layout.Children.Add(new Border
        {
            Child = _remotePanel,
            Padding = new Thickness(2),
        });

        // ── 已屏蔽区 ──────────────────────────────
        layout.Children.Add(new TextBlock
        {
            Text = "已屏蔽（因加载失败被自动禁用）",
            FontWeight = FontWeights.SemiBold,
        });
        layout.Children.Add(_disabledStatus);
        layout.Children.Add(_disabledPanel);

        scroll.Content = layout;
        Content = scroll;

        Loaded += async (_, _) =>
        {
            await RenderInstalledAsync();
            await LoadRemoteAsync();
            RenderDisabled();
        };
    }

    private async Task LoadRemoteAsync()
    {
        _refreshButton.IsEnabled = false;
        _remoteStatus.Text = "正在从 DSH Market 获取插件列表…";
        _remotePanel.Children.Clear();
        try
        {
            var plugins = await PluginManager.FetchRemotePluginsAsync();
            if (plugins.Count == 0)
            {
                _remoteStatus.Text = "未获取到插件（网络或数据源问题），可点击刷新重试。";
                return;
            }
            _remoteStatus.Text = $"共 {plugins.Count} 个可一键安装插件（按实用分排序）";
            foreach (var p in plugins)
            {
                _remotePanel.Children.Add(BuildRemoteRow(p));
            }
        }
        catch (Exception ex)
        {
            _remoteStatus.Text = $"获取插件失败：{ex.Message}";
        }
        finally
        {
            _refreshButton.IsEnabled = true;
        }
    }

    private void RenderDisabled()
    {
        _disabledPanel.Children.Clear();
        var disabled = _settings.DisabledPlugins.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (disabled.Count == 0)
        {
            _disabledStatus.Text = "无";
            return;
        }
        _disabledStatus.Text = $"{disabled.Count} 个插件被屏蔽";
        foreach (var pkg in disabled)
        {
            _disabledPanel.Children.Add(BuildDisabledRow(pkg));
        }
    }

    private async Task RenderInstalledAsync()
    {
        _installedPanel.Children.Clear();
        var installed = await PluginManager.GetInstalledPluginsAsync();
        if (installed.Count == 0)
        {
            _installedStatus.Text = "无";
            return;
        }
        _installedStatus.Text = $"{installed.Count} 个（含内置模板 bundle）";
        foreach (var pkg in installed)
        {
            _installedPanel.Children.Add(BuildInstalledRow(pkg));
        }
    }

    private Border BuildInstalledRow(string packageName)
    {
        var isBuiltin = BuiltinBundles.Contains(packageName);
        var name = new TextBlock
        {
            Text = packageName,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (isBuiltin)
        {
            right.Children.Add(new TextBlock { Text = "内置", FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        }
        var uninstall = new Button
        {
            Content = "卸载",
            MinWidth = 72,
            IsEnabled = !isBuiltin,
            VerticalAlignment = VerticalAlignment.Center,
        };
        uninstall.Click += async (_, _) => await UninstallAsync(packageName, uninstall);
        right.Children.Add(uninstall);

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(name);
        grid.Children.Add(right);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
    }

    private async Task UninstallAsync(string packageName, Button button)
    {
        button.IsEnabled = false;
        button.Content = "卸载中…";
        try
        {
            var result = await PluginManager.RemoveAsync(packageName);
            if (result.Success)
            {
                button.Content = "已卸载 ✓";
                await RenderInstalledAsync();
                PluginsChanged?.Invoke();
            }
            else
            {
                await ShowMessageAsync($"卸载 {packageName} 失败", result.Output);
                button.IsEnabled = true;
                button.Content = "卸载";
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("卸载出错", ex.Message);
            button.IsEnabled = true;
            button.Content = "卸载";
        }
    }

    private Border BuildRemoteRow(RemotePlugin plugin)
    {
        var name = new TextBlock
        {
            Text = plugin.PackageName,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var meta = new TextBlock
        {
            Text = $"{(plugin.NeedsConfig ? "需配置 · " : "")}{plugin.RepoFullName} · ⭐{plugin.Stars} · 实用分 {plugin.Score}",
            FontSize = 11,
            Opacity = 0.6,
        };
        var desc = new TextBlock
        {
            Text = plugin.Description ?? "（无描述）",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(name);
        left.Children.Add(meta);
        left.Children.Add(desc);

        var install = new Button { Content = "安装", MinWidth = 72, VerticalAlignment = VerticalAlignment.Center };
        install.Click += async (_, _) => await InstallAsync(plugin, install);

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(install, 1);
        grid.Children.Add(left);
        grid.Children.Add(install);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
    }

    private Border BuildDisabledRow(string packageName)
    {
        var name = new TextBlock
        {
            Text = packageName,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var restore = new Button
        {
            Content = "恢复",
            MinWidth = 72,
            VerticalAlignment = VerticalAlignment.Center,
        };
        restore.Click += async (_, _) => await RestoreAsync(packageName, restore);

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 0);
        Grid.SetColumn(restore, 1);
        grid.Children.Add(name);
        grid.Children.Add(restore);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
    }

    private async Task InstallAsync(RemotePlugin plugin, Button button)
    {
        button.IsEnabled = false;
        button.Content = "安装中…";
        try
        {
            var result = await PluginManager.InstallAsync(plugin.InstallSpec);
            if (result.Success)
            {
                button.Content = "已安装 ✓";
                _settings.DisabledPlugins.Remove(plugin.PackageName);
                _settings.Save();
                await RenderInstalledAsync();
                RenderDisabled();
                PluginsChanged?.Invoke();
            }
            else
            {
                button.Content = "安装失败";
                await ShowMessageAsync($"安装 {plugin.PackageName} 失败", result.Output);
                button.IsEnabled = true;
                button.Content = "安装";
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("安装出错", ex.Message);
            button.IsEnabled = true;
            button.Content = "安装";
        }
    }

    private async Task RestoreAsync(string packageName, Button button)
    {
        button.IsEnabled = false;
        button.Content = "恢复中…";
        try
        {
            var ok = await PluginManager.EnablePluginAsync(_settings, packageName);
            if (ok)
            {
                button.Content = "已恢复 ✓";
                await RenderInstalledAsync();
                RenderDisabled();
                PluginsChanged?.Invoke();
            }
            else
            {
                await ShowMessageAsync($"恢复 {packageName} 失败", "请检查网络或 npm registry 状态后重试。");
                button.IsEnabled = true;
                button.Content = "恢复";
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("恢复出错", ex.Message);
            button.IsEnabled = true;
            button.Content = "恢复";
        }
    }

    private Task ShowMessageAsync(string title, string detail)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 300,
                Content = new TextBlock { Text = string.IsNullOrWhiteSpace(detail) ? "（无输出）" : detail, TextWrapping = TextWrapping.Wrap },
            },
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        };
        return dialog.ShowAsync().AsTask();
    }
}

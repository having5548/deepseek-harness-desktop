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
    private readonly StackPanel _remotePanel = new() { Spacing = 8 };
    private readonly TextBlock _remoteStatus = new() { FontSize = 12, Opacity = 0.7 };
    private readonly StackPanel _disabledPanel = new() { Spacing = 8 };
    private readonly TextBlock _disabledStatus = new() { FontSize = 12, Opacity = 0.7 };
    private readonly Button _refreshButton = new() { Content = "刷新列表" };

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
        var layout = new StackPanel { Spacing = 14, Width = 540 };

        // ── 远程插件区 ────────────────────────────
        var remoteHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        remoteHeader.Children.Add(new TextBlock
        {
            Text = "从 GitHub「dsh-plugin」主题发现",
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
            await LoadRemoteAsync();
            RenderDisabled();
        };
    }

    private async Task LoadRemoteAsync()
    {
        _refreshButton.IsEnabled = false;
        _remoteStatus.Text = "正在从 GitHub 获取插件列表…";
        _remotePanel.Children.Clear();
        try
        {
            var plugins = await PluginManager.FetchRemotePluginsAsync();
            if (plugins.Count == 0)
            {
                _remoteStatus.Text = "未获取到插件（网络或限流问题），可点击刷新重试。";
                return;
            }
            _remoteStatus.Text = $"共 {plugins.Count} 个候选插件（按 Stars 排序，已过滤非插件仓库）";
            foreach (var p in plugins.OrderByDescending(p => p.Stars))
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
            Text = $"{plugin.RepoFullName} · ⭐ {plugin.Stars}",
            FontSize = 11,
            Opacity = 0.6,
        };
        var desc = new TextBlock
        {
            Text = plugin.Description ?? "（无描述）",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
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
            var result = await PluginManager.InstallAsync(plugin.PackageName);
            if (result.Success)
            {
                button.Content = "已安装 ✓";
                _settings.DisabledPlugins.Remove(plugin.PackageName);
                _settings.Save();
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DshDesktop.Services;

/// <summary>
/// 插件管理对话框：从多个可信来源（DSH Market / npm 官方 / npmmirror）获取插件，
/// 支持「单来源 / 多来源叠加」切换、按来源标识、GitHub 仓库一键跳转、正则搜索插件名；
/// 整合列表持久化到本地缓存，全部来源不可用时自动回退显示缓存。
/// </summary>
public sealed class PluginDialog : ContentDialog
{
    /// <summary>有插件被安装或恢复，主窗口应重启服务。</summary>
    public event Action? PluginsChanged;

    private readonly MainWindow _owner;
    private readonly AppSettings _settings;

    private readonly ComboBox _modeCombo = new() { Width = 170 };
    private readonly ComboBox _singleSourceCombo = new() { MinWidth = 150 };
    private readonly StackPanel _multiSourcesPanel = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBox _searchBox = new()
    {
        PlaceholderText = "搜索插件名（支持正则）",
        Width = 220,
    };
    private readonly Button _refreshButton = new() { Content = "刷新列表" };

    private readonly StackPanel _installedPanel = new() { Spacing = 8 };
    private readonly TextBlock _installedStatus = new() { FontSize = 12, Opacity = 0.7 };
    private readonly StackPanel _remotePanel = new() { Spacing = 8 };
    private readonly TextBlock _remoteStatus = new() { FontSize = 12, Opacity = 0.7 };
    private readonly StackPanel _disabledPanel = new() { Spacing = 8 };
    private readonly TextBlock _disabledStatus = new() { FontSize = 12, Opacity = 0.7 };

    private List<PluginCacheEntry> _currentPlugins = new();
    private string _listHeader = "";
    private bool _loading;

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
            MaxHeight = 460,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var layout = new StackPanel { Spacing = 14, MinWidth = 420, MaxWidth = 560 };

        // ── 已安装插件区 ──────────────────────────
        layout.Children.Add(new TextBlock { Text = "已安装插件", FontWeight = FontWeights.SemiBold });
        layout.Children.Add(_installedStatus);
        layout.Children.Add(new Border { Child = _installedPanel, Padding = new Thickness(2) });

        // ── 插件来源区 ────────────────────────────
        layout.Children.Add(new TextBlock { Text = "从插件来源获取", FontWeight = FontWeights.SemiBold });

        _modeCombo.Items.Add(new ComboBoxItem { Content = "多来源叠加（推荐）", Tag = "multi" });
        _modeCombo.Items.Add(new ComboBoxItem { Content = "单来源", Tag = "single" });
        _modeCombo.SelectionChanged += async (_, _) =>
        {
            if (_modeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _settings.PluginSourceMode = tag;
                _settings.Save();
                UpdateSourceControls();
                await LoadRemoteAsync();
            }
        };

        foreach (var s in PluginManager.AllSources)
        {
            _singleSourceCombo.Items.Add(new ComboBoxItem { Content = s.Name, Tag = s.Id });
            var toggle = new ToggleButton { Content = s.Name, Tag = s.Id, MinWidth = 84 };
            toggle.Click += async (sender, _) =>
            {
                var id = (string)((ToggleButton)sender).Tag;
                if (((ToggleButton)sender).IsChecked == true)
                {
                    if (!_settings.EnabledPluginSources.Contains(id))
                    {
                        _settings.EnabledPluginSources.Add(id);
                    }
                }
                else
                {
                    _settings.EnabledPluginSources.Remove(id);
                }
                _settings.Save();
                await LoadRemoteAsync();
            };
            _multiSourcesPanel.Children.Add(toggle);
        }
        _singleSourceCombo.SelectionChanged += async (_, _) =>
        {
            if (_singleSourceCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _settings.SelectedPluginSource = tag;
                _settings.Save();
                await LoadRemoteAsync();
            }
        };

        _refreshButton.Click += async (_, _) => await LoadRemoteAsync();
        _searchBox.TextChanged += (_, _) => ApplySearchFilter();

        var sourceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sourceRow.Children.Add(_modeCombo);
        sourceRow.Children.Add(_singleSourceCombo);
        sourceRow.Children.Add(_multiSourcesPanel);

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actionRow.Children.Add(_searchBox);
        actionRow.Children.Add(_refreshButton);

        layout.Children.Add(sourceRow);
        layout.Children.Add(actionRow);
        layout.Children.Add(_remoteStatus);
        layout.Children.Add(new TextBlock
        {
            Text = "安装后自动重启服务生效；若插件导致服务崩溃会被自动屏蔽。",
            FontSize = 11,
            Opacity = 0.6,
        });
        layout.Children.Add(new Border { Child = _remotePanel, Padding = new Thickness(2) });

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
            ApplyModeFromSettings();
            UpdateSourceControls();
            await RenderInstalledAsync();
            await LoadRemoteAsync();
            RenderDisabled();
        };
    }

    private void ApplyModeFromSettings()
    {
        var mode = _settings.PluginSourceMode == "single" ? "single" : "multi";
        foreach (ComboBoxItem item in _modeCombo.Items)
        {
            if (item.Tag is string tag && tag == mode)
            {
                _modeCombo.SelectedItem = item;
                break;
            }
        }
    }

    /// <summary>按设置刷新来源控件状态（单源下拉框选中项、多源开关勾选、可见性）。</summary>
    private void UpdateSourceControls()
    {
        var isMulti = _settings.PluginSourceMode != "single";
        _singleSourceCombo.Visibility = isMulti ? Visibility.Collapsed : Visibility.Visible;
        _multiSourcesPanel.Visibility = isMulti ? Visibility.Visible : Visibility.Collapsed;

        if (!isMulti)
        {
            var selected = PluginManager.FindSource(_settings.SelectedPluginSource) ?? PluginManager.AllSources[0];
            foreach (ComboBoxItem item in _singleSourceCombo.Items)
            {
                if (item.Tag is string tag && tag == selected.Id)
                {
                    _singleSourceCombo.SelectedItem = item;
                    break;
                }
            }
        }
        else
        {
            foreach (var child in _multiSourcesPanel.Children)
            {
                if (child is ToggleButton tb && tb.Tag is string id)
                {
                    tb.IsChecked = _settings.EnabledPluginSources.Contains(id);
                }
            }
        }
    }

    /// <summary>解析当前生效的来源列表。</summary>
    private List<PluginSource> ResolveActiveSources()
    {
        if (_settings.PluginSourceMode == "single")
        {
            var s = PluginManager.FindSource(_settings.SelectedPluginSource) ?? PluginManager.AllSources[0];
            return new List<PluginSource> { s };
        }
        var enabled = _settings.EnabledPluginSources;
        var sources = PluginManager.AllSources
            .Where(s => enabled.Contains(s.Id))
            .ToList();
        return sources.Count > 0 ? sources : new List<PluginSource> { PluginManager.AllSources[0] };
    }

    private async Task LoadRemoteAsync()
    {
        if (_loading)
        {
            return;
        }
        _loading = true;
        _refreshButton.IsEnabled = false;
        _remotePanel.Children.Clear();
        try
        {
            var sources = ResolveActiveSources();
            _listHeader = $"正在从 {string.Join("、", sources.Select(s => s.Name))} 获取插件…";
            _remoteStatus.Text = _listHeader;

            var result = await PluginManager.FetchAsync(sources);
            var merged = PluginManager.MergeAndDedupe(result.Plugins);
            if (merged.Count > 0)
            {
                // 刷新成功：写入持久化缓存（单一文件）
                var cache = new PluginCacheFile { Sources = sources.Select(s => s.Name).ToList() };
                cache.Plugins.AddRange(merged);
                await PluginCache.SaveAsync(cache);

                _currentPlugins = merged;
                var failedNote = result.FailedSources.Count > 0
                    ? $"（获取失败：{string.Join("、", result.FailedSources)}）"
                    : "";
                _listHeader = $"共 {merged.Count} 个插件，来自：{string.Join("、", sources.Select(s => s.Name))}{failedNote}";
            }
            else
            {
                await ShowFromCacheAsync("所有插件源均获取失败");
            }
        }
        catch (Exception ex)
        {
            await ShowFromCacheAsync($"获取插件出错：{ex.Message}");
        }
        finally
        {
            _loading = false;
            _refreshButton.IsEnabled = true;
        }
        ApplySearchFilter();
    }

    /// <summary>全部来源不可用时回退到本地缓存。</summary>
    private async Task ShowFromCacheAsync(string reason)
    {
        var cache = await PluginCache.LoadAsync();
        if (cache is null)
        {
            _currentPlugins = new List<PluginCacheEntry>();
            _listHeader = $"{reason}，且本地无缓存。可检查网络后点击刷新重试。";
        }
        else
        {
            _currentPlugins = cache.Plugins;
            _listHeader = $"{reason}，已显示本地缓存（保存于 {cache.SavedAt}，来自：{string.Join("、", cache.Sources)}）。";
        }
    }

    /// <summary>按搜索框中的正则表达式过滤插件名并重绘列表。</summary>
    private void ApplySearchFilter()
    {
        var pattern = _searchBox.Text.Trim();
        IEnumerable<PluginCacheEntry> list = _currentPlugins;
        var invalidRegex = false;
        if (pattern.Length > 0)
        {
            try
            {
                var re = new Regex(pattern, RegexOptions.IgnoreCase);
                list = list.Where(p => re.IsMatch(p.PackageName));
            }
            catch
            {
                invalidRegex = true;
            }
        }
        var filtered = list.ToList();
        _remotePanel.Children.Clear();
        foreach (var p in filtered)
        {
            _remotePanel.Children.Add(BuildRemoteRow(p));
        }
        _remoteStatus.Text = invalidRegex
            ? $"{_listHeader} · 正则无效，已显示全部"
            : pattern.Length > 0
                ? $"{_listHeader} · 搜索命中 {filtered.Count} 个"
                : _listHeader;
    }

    // ── 列表行构建 ─────────────────────────────────────

    private Border BuildRemoteRow(PluginCacheEntry plugin)
    {
        var name = new TextBlock
        {
            Text = plugin.PackageName,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var metaParts = new List<string>();
        if (plugin.Sources.Count > 0)
        {
            metaParts.Add($"来源：{string.Join("、", plugin.Sources)}");
        }
        if (!string.IsNullOrEmpty(plugin.RepoFullName))
        {
            metaParts.Add(plugin.RepoFullName);
        }
        if (plugin.Stars > 0)
        {
            metaParts.Add($"⭐{plugin.Stars}");
        }
        if (plugin.Score > 0)
        {
            metaParts.Add($"实用分 {plugin.Score}");
        }
        if (plugin.NeedsConfig)
        {
            metaParts.Add("需配置");
        }
        var meta = new TextBlock
        {
            Text = string.Join(" · ", metaParts),
            FontSize = 11,
            Opacity = 0.6,
        };

        var desc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(plugin.Description) ? "（无描述）" : plugin.Description,
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

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

        if (!string.IsNullOrEmpty(plugin.RepoUrl))
        {
            var gh = new Button
            {
                MinWidth = 72,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(gh, "打开 GitHub 仓库");
            var ghContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            ghContent.Children.Add(new FontIcon { Glyph = "\uE8A7", FontSize = 12 });
            ghContent.Children.Add(new TextBlock { Text = "GitHub", FontSize = 12 });
            gh.Content = ghContent;
            var url = plugin.RepoUrl;
            gh.Click += (_, _) => OpenUrl(url);
            right.Children.Add(gh);
        }

        var install = new Button { Content = "安装", MinWidth = 72, VerticalAlignment = VerticalAlignment.Center };
        install.Click += async (_, _) => await InstallAsync(plugin, install);
        right.Children.Add(install);

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
    }

    private static void OpenUrl(string url)
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

    // ── 安装 / 卸载 / 恢复 ─────────────────────────────

    private async Task InstallAsync(PluginCacheEntry plugin, Button button)
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

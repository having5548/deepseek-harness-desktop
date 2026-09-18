using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DshDesktop.Services;

/// <summary>
/// 设置对话框：允许用户指定 dsh 可执行文件（dsh.cmd / dsh.exe / bin.js），
/// 或留空以使用自动检测。
/// </summary>
public sealed class SettingsDialog : ContentDialog
{
    private readonly MainWindow _owner;
    private readonly TextBox _pathBox = new()
    {
        PlaceholderText = "留空则自动检测（自动安装目录或 PATH）",
    };

    /// <summary>用户最终确认的 dsh 路径（可能为空 = 自动检测）。</summary>
    public string DshPath => _pathBox.Text.Trim();

    /// <summary>升级 dsh 后是否刷新 web profile 的插件树。</summary>
    public bool RefreshProfileAfterUpdate => _refreshSwitch.IsOn;

    private readonly ToggleSwitch _refreshSwitch = new()
    {
        Header = "升级 dsh 后刷新插件树",
        OnContent = "开启",
        OffContent = "关闭",
    };

    public SettingsDialog(string? currentPath, string? currentVersion, bool refreshProfileAfterUpdate, MainWindow owner)
    {
        _owner = owner;
        Title = "设置";
        PrimaryButtonText = "保存";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;

        _pathBox.Text = currentPath ?? string.Empty;
        _pathBox.Width = 380;
        _refreshSwitch.IsOn = refreshProfileAfterUpdate;

        var browse = new Button { Content = "浏览…" };
        browse.Click += async (_, _) => await BrowseAsync();

        var detect = new Button { Content = "自动检测" };
        detect.Click += (_, _) => _pathBox.Text = string.Empty;

        var note = new TextBlock
        {
            Text = "dsh 是 DeepSeek Harness 命令行入口。\n首次启动会自动安装到：" +
                   DshPaths.InstallRoot + "\n也可以手动指定已存在的 dsh 路径（npm install -g @deepseek-ai/dsh）。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        };

        var versionNote = new TextBlock
        {
            Text = currentVersion is null ? "当前 dsh 版本：未安装" : $"当前 dsh 版本：{currentVersion}",
            FontSize = 12,
            Opacity = 0.7,
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(browse);
        buttons.Children.Add(detect);

        var refreshNote = new TextBlock
        {
            Text = "插件树由 ~/.dsh/profiles/web 下的 pnpm 独立管理，升级 dsh 不会自动重解析。" +
                   "开启后，每次升级完成会在该目录执行一次 pnpm update，避免插件与 CLI 版本不匹配。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            MaxWidth = 400,
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 400 };
        panel.Children.Add(new TextBlock { Text = "dsh 可执行文件路径" });
        panel.Children.Add(_pathBox);
        panel.Children.Add(buttons);
        panel.Children.Add(versionNote);
        panel.Children.Add(note);
        panel.Children.Add(new Border
        {
            Height = 1,
            Background = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Microsoft.UI.Xaml.Thickness(0, 6, 0, 6),
        });
        panel.Children.Add(_refreshSwitch);
        panel.Children.Add(refreshNote);

        Content = panel;
    }

    private async Task BrowseAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add(".cmd");
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".js");

        var hwnd = WindowNative.GetWindowHandle(_owner);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            _pathBox.Text = file.Path;
        }
    }
}

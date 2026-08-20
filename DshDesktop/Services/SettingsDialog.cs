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
        PlaceholderText = "留空则自动检测（PATH 或 npm 全局安装）",
    };

    private readonly CheckBox _checkUpdatesBox = new()
    {
        Content = "启动时自动检查 dsh 更新",
    };

    /// <summary>用户最终确认的 dsh 路径（可能为空 = 自动检测）。</summary>
    public string DshPath => _pathBox.Text.Trim();

    /// <summary>是否在启动时自动检查 dsh 更新。</summary>
    public bool CheckForUpdates => _checkUpdatesBox.IsChecked == true;

    public SettingsDialog(string? currentPath, bool checkForUpdates, string? currentVersion, MainWindow owner)
    {
        _owner = owner;
        Title = "设置";
        PrimaryButtonText = "保存";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;

        _pathBox.Text = currentPath ?? string.Empty;
        _pathBox.Width = 380;
        _checkUpdatesBox.IsChecked = checkForUpdates;

        var browse = new Button { Content = "浏览…" };
        browse.Click += async (_, _) => await BrowseAsync();

        var detect = new Button { Content = "自动检测" };
        detect.Click += (_, _) => _pathBox.Text = string.Empty;

        var note = new TextBlock
        {
            Text = "dsh 是 DeepSeek Harness 命令行入口。\n安装方式：npm install -g @deepseek-ai/dsh",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        };

        var versionNote = new TextBlock
        {
            Text = currentVersion is null ? "当前 dsh 版本：未知" : $"当前 dsh 版本：{currentVersion}",
            FontSize = 12,
            Opacity = 0.7,
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(browse);
        buttons.Children.Add(detect);

        var panel = new StackPanel { Spacing = 10, MinWidth = 400 };
        panel.Children.Add(new TextBlock { Text = "dsh 可执行文件路径" });
        panel.Children.Add(_pathBox);
        panel.Children.Add(buttons);
        panel.Children.Add(_checkUpdatesBox);
        panel.Children.Add(versionNote);
        panel.Children.Add(note);

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

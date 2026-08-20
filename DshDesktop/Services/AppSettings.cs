using System;
using System.IO;
using System.Text.Json;

namespace DshDesktop.Services;

/// <summary>
/// 本地用户设置，持久化到 <c>%APPDATA%\DshDesktop\settings.json</c>。
/// 目前仅保存用户手动指定的 dsh 可执行文件路径（为空表示自动检测）。
/// </summary>
public sealed class AppSettings
{
    public string? DshPath { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DshDesktop",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // 设置损坏时回退到默认值
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 保存失败不影响应用运行
        }
    }
}

//! 本地用户设置（C# 版 AppSettings 移植），持久化到 `<设置目录>/DshDesktop/settings.json`。
//! 目录名沿用 C# 版，旧版设置可无缝继承。

use serde::{Deserialize, Serialize};
use std::path::PathBuf;

use crate::paths;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct AppSettings {
    /// 用户手动指定的 dsh 路径（空 = 自动检测）。
    pub dsh_path: String,
    /// 因加载失败被自动卸载的插件历史（npm 包名，去重）。
    /// 仅供插件管理展示（只读），不再支持“恢复”。
    /// 注：JSON 键仍为 `DisabledPlugins`（`rename_all = "PascalCase"`），
    /// 且字段名不改动，以便无缝继承 C# 版/旧版设置文件。
    pub disabled_plugins: Vec<String>,
    /// 插件来源模式："multi" = 多来源叠加（默认），"single" = 单来源。
    pub plugin_source_mode: String,
    /// 单来源模式下选中的来源 Id（见 plugins::all_sources）。
    pub selected_plugin_source: String,
    /// 多来源叠加模式下启用的来源 Id 列表。
    pub enabled_plugin_sources: Vec<String>,
    /// 升级 dsh 之后是否刷新 web profile 的插件树（等价于在 profile 目录执行 pnpm update）。
    pub refresh_profile_after_update: bool,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            dsh_path: String::new(),
            disabled_plugins: Vec::new(),
            plugin_source_mode: "multi".into(),
            selected_plugin_source: "dsh-market".into(),
            enabled_plugin_sources: vec![
                "dsh-market".into(),
                "npm".into(),
                "npmmirror".into(),
            ],
            refresh_profile_after_update: true,
        }
    }
}

pub fn settings_file() -> PathBuf {
    paths::settings_dir().join("settings.json")
}

impl AppSettings {
    pub fn load() -> Self {
        match std::fs::read(settings_file()) {
            Ok(bytes) => serde_json::from_slice(&bytes).unwrap_or_default(),
            Err(_) => Self::default(),
        }
    }

    pub fn save(&self) -> std::io::Result<()> {
        let path = settings_file();
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let json = serde_json::to_string_pretty(self).unwrap_or_else(|_| "{}".into());
        std::fs::write(path, json)
    }

    /// 该包是否已有自动卸载记录（仅用于去重，**不是**加载屏蔽判断）。
    fn is_uninstall_recorded(&self, package: &str) -> bool {
        self.disabled_plugins.iter().any(|p| p == package)
    }

    /// 记录一条“因加载失败被自动卸载”的历史（去重）。
    /// 该列表是只读历史，供插件管理查看，不再支持“恢复”。
    ///
    /// 注意：本方法**不**阻止插件被加载 —— 阻断崩溃的方法是
    /// `plugins::purge_plugin_residue` 摘掉 `package.json` 的 `dsh.profile.bundles` 条目。
    pub fn record_auto_uninstall(&mut self, package: &str) {
        if !self.is_uninstall_recorded(package) {
            self.disabled_plugins.push(package.to_string());
        }
    }

    /// 清空自动卸载历史。
    pub fn clear_disabled_history(&mut self) {
        self.disabled_plugins.clear();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrip_defaults() {
        let s = AppSettings::default();
        let json = serde_json::to_string(&s).unwrap();
        let back: AppSettings = serde_json::from_str(&json).unwrap();
        assert_eq!(back.plugin_source_mode, "multi");
        assert!(back.refresh_profile_after_update);
        assert_eq!(back.enabled_plugin_sources.len(), 3);
    }

    #[test]
    fn tolerates_missing_fields() {
        let back: AppSettings = serde_json::from_str("{}").unwrap();
        assert_eq!(back.selected_plugin_source, "dsh-market");
        assert!(back.disabled_plugins.is_empty());
    }

    #[test]
    fn disable_records_history() {
        let mut s = AppSettings::default();
        s.record_auto_uninstall("pkg-a");
        s.record_auto_uninstall("pkg-a"); // 去重
        s.record_auto_uninstall("pkg-b");
        assert_eq!(s.disabled_plugins.len(), 2);
        assert!(s.is_uninstall_recorded("pkg-a"));
        s.clear_disabled_history();
        assert!(s.disabled_plugins.is_empty());
    }
}

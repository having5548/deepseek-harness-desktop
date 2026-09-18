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
    /// 因加载失败被自动屏蔽的插件（npm 包名）。
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

    pub fn is_disabled(&self, package: &str) -> bool {
        self.disabled_plugins.iter().any(|p| p == package)
    }

    pub fn disable(&mut self, package: &str) {
        if !self.is_disabled(package) {
            self.disabled_plugins.push(package.to_string());
        }
    }

    pub fn enable(&mut self, package: &str) {
        self.disabled_plugins.retain(|p| p != package);
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
    fn disable_enable() {
        let mut s = AppSettings::default();
        s.disable("pkg-a");
        s.disable("pkg-a");
        assert_eq!(s.disabled_plugins.len(), 1);
        s.enable("pkg-a");
        assert!(s.disabled_plugins.is_empty());
    }
}

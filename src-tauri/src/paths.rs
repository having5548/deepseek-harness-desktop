//! 应用路径常量（C# 版 DshPaths 的跨平台移植）。
//!
//! 设计说明：安装包只捆绑 Node 运行时（node + npm + pnpm），**不捆绑 dsh**。
//! dsh 首次启动时用捆绑的 node+npm 联网安装到 `install_root()`，装完自动被
//! [`crate::locator`] 定位并绑定，此后启动直接复用。

use std::path::{Path, PathBuf};

/// 应用安装目录（exe 所在目录）。
pub fn app_dir() -> PathBuf {
    std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|p| p.to_path_buf()))
        .unwrap_or_else(|| PathBuf::from("."))
}

/// 捆绑运行时目录。优先 Tauri 资源目录，其次 exe 同级目录（与 C# 版一致），
/// 开发期兜底到 src-tauri/resources/runtime。
pub fn bundled_runtime_dir() -> PathBuf {
    if let Ok(dir) = std::env::var("DSH_RUNTIME_DIR") {
        if !dir.trim().is_empty() {
            return PathBuf::from(dir);
        }
    }
    let mut candidates: Vec<PathBuf> = Vec::new();
    // tauri::path 需要 App 句柄，这里用启发式解析（避免把 PathResolver 传来传去）
    if let Ok(exe) = std::env::current_exe() {
        if let Some(parent) = exe.parent() {
            candidates.push(parent.join("resources").join("runtime"));
            candidates.push(parent.join("runtime"));
            // dev: target/debug 或 target/release 下往回找
            candidates.push(parent.join("../../src-tauri/resources/runtime"));
        }
    }
    candidates.push(PathBuf::from("resources/runtime"));
    for c in candidates.iter() {
        if c.join(node_binary_name()).exists() {
            return c.clone();
        }
    }
    candidates.first().cloned().unwrap_or_default()
}

pub fn node_binary_name() -> &'static str {
    if cfg!(windows) {
        "node.exe"
    } else {
        "node"
    }
}

pub fn bundled_node() -> PathBuf {
    bundled_runtime_dir().join(node_binary_name())
}

pub fn bundled_npm_cli() -> PathBuf {
    bundled_runtime_dir()
        .join("node_modules")
        .join("npm")
        .join("bin")
        .join("npm-cli.js")
}

/// dsh 安装根目录：
/// - 环境变量 `DSH_INSTALL_ROOT` 最优先（测试 / 高级用法）；
/// - Windows：应用所在盘根的 `DeepSeek Harness` 文件夹（与 C# 版一致）。
///   若应用所在盘没有已装好的 dsh，会扫描其他盘符找回旧安装
///   （例如旧 C# 版应用装在 H 盘、新版装到 C 盘时，直接复用 `H:\DeepSeek Harness`，
///   不重新下载）；都没有才在应用所在盘新建；
/// - Linux/macOS：`$XDG_DATA_HOME`（默认 `~/.local/share`）下的 `DeepSeek Harness`。
pub fn install_root() -> PathBuf {
    if let Ok(root) = std::env::var("DSH_INSTALL_ROOT") {
        if !root.trim().is_empty() {
            return PathBuf::from(root);
        }
    }
    if cfg!(windows) {
        // ancestors().last() 即盘符根（如 "H:\"）
        let drive_root = app_dir()
            .ancestors()
            .last()
            .unwrap_or_else(|| Path::new("C:\\"))
            .to_path_buf();
        let app_drive_root = drive_root.join("DeepSeek Harness");

        // 1. 应用所在盘已有 dsh（C# 版绑定规则，优先级最高）
        if is_managed_install_at(&app_drive_root) {
            return app_drive_root;
        }

        // 2. 其他盘符上已有 dsh → 复用旧安装（应用换盘后不至于重复下载一份）
        for letter in b'B'..=b'Z' {
            let candidate = Path::new(&format!("{}:\\", letter as char)).join("DeepSeek Harness");
            if candidate == app_drive_root {
                continue;
            }
            if is_managed_install_at(&candidate) {
                return candidate;
            }
        }

        // 3. 全新安装 → 应用所在盘（与 C# 版一致）
        app_drive_root
    } else {
        data_home().join("DeepSeek Harness")
    }
}

/// 判定某目录是否为一次完整可用的 dsh 托管安装（以 bin.js 存在为准，与定位逻辑一致）。
fn is_managed_install_at(root: &Path) -> bool {
    root.join("node_modules")
        .join("@deepseek-ai")
        .join("dsh")
        .join("lib")
        .join("bin.js")
        .is_file()
}

/// npm 把 dsh 装到安装根目录下的 node_modules/@deepseek-ai/dsh。
pub fn dsh_package_dir() -> PathBuf {
    install_root()
        .join("node_modules")
        .join("@deepseek-ai")
        .join("dsh")
}

pub fn dsh_bin_script() -> PathBuf {
    dsh_package_dir().join("lib").join("bin.js")
}

pub fn dsh_package_json() -> PathBuf {
    dsh_package_dir().join("package.json")
}

/// dsh 是否已安装到安装根目录（bin.js 存在即视为已安装）。
pub fn is_dsh_installed() -> bool {
    dsh_bin_script().is_file()
}

/// dsh 的 home 目录：环境变量 `DSH_HOME` 优先，否则 `~/.dsh`（与 dsh 自身规则一致）。
pub fn dsh_home() -> PathBuf {
    if let Ok(home) = std::env::var("DSH_HOME") {
        if !home.trim().is_empty() {
            return PathBuf::from(home.trim());
        }
    }
    user_home_dir().join(".dsh")
}

/// `$DSH_HOME/profiles/node_modules` —— dsh 的"模块回退"缓存（符号链接农场）。
/// 纯派生缓存：删掉后 dsh 下次启动会依据当前安装重新生成。
pub fn profile_module_fallback_dir() -> PathBuf {
    dsh_home().join("profiles").join("node_modules")
}

/// 安装目录中被回退缓存镜像的包作用域目录（dsh 的依赖闭包都在这里）。
pub fn installed_scope_dir() -> PathBuf {
    dsh_package_dir()
        .join("node_modules")
        .join("@deepseek-ai")
}

/// 捆绑运行时是否完整（node + npm 都可用）。
pub fn is_bundled_runtime_complete() -> bool {
    bundled_node().is_file() && bundled_npm_cli().is_file()
}

/// 用户设置目录（沿用 C# 版的 `DshDesktop` 目录名，旧设置可无缝迁移）：
/// Windows `%APPDATA%\DshDesktop`；Unix `$XDG_CONFIG_HOME|~/.config` + `DshDesktop`。
pub fn settings_dir() -> PathBuf {
    if cfg!(windows) {
        match std::env::var("APPDATA") {
            Ok(v) if !v.trim().is_empty() => PathBuf::from(v).join("DshDesktop"),
            _ => user_home_dir().join("AppData").join("Roaming").join("DshDesktop"),
        }
    } else {
        let base = match std::env::var("XDG_CONFIG_HOME") {
            Ok(v) if !v.trim().is_empty() => PathBuf::from(v),
            _ => user_home_dir().join(".config"),
        };
        base.join("DshDesktop")
    }
}

fn data_home() -> PathBuf {
    match std::env::var("XDG_DATA_HOME") {
        Ok(v) if !v.trim().is_empty() => PathBuf::from(v),
        _ => user_home_dir().join(".local").join("share"),
    }
}

pub fn user_home_dir() -> PathBuf {
    std::env::var(if cfg!(windows) { "USERPROFILE" } else { "HOME" })
        .map(PathBuf::from)
        .unwrap_or_else(|_| PathBuf::from("."))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn install_root_env_override() {
        std::env::set_var("DSH_INSTALL_ROOT", "/tmp/dsh-test-root");
        assert_eq!(install_root(), PathBuf::from("/tmp/dsh-test-root"));
        std::env::remove_var("DSH_INSTALL_ROOT");
    }

    #[test]
    fn dsh_home_respects_env() {
        std::env::set_var("DSH_HOME", "/tmp/dsh-home-test");
        assert_eq!(dsh_home(), PathBuf::from("/tmp/dsh-home-test"));
        std::env::remove_var("DSH_HOME");
    }

    #[test]
    fn paths_are_consistent() {
        assert!(dsh_bin_script()
            .to_string_lossy()
            .contains("node_modules"));
        assert!(bundled_npm_cli().ends_with("npm-cli.js"));
    }
}

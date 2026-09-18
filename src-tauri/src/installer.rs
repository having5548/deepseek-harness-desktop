//! dsh 安装 / 升级执行器（C# 版 DshInstaller 移植）：
//! 把 npm 安装结果先写到独立暂存目录，校验通过后再整体替换到安装根目录。
//!
//! **为什么不再就地 npm install：**
//! 过去直接在安装根目录上跑 npm install，若旧目录里有被 dsh 进程占用（Windows 文件锁）
//! 或新版本已删除的文件，npm 会跳过后继续 —— 于是留下"半新半旧"的坏安装。
//! 现在改为 暂存安装 → 版本校验 → 整体替换，失败则原样回滚。
//!
//! **为什么必须锁死确切版本号：**
//! dsh 的 npm 标签 latest 可能比 next 更旧，而它声明的依赖范围 ^x.y.z-rc.1 会被 npm
//! 解析到该范围内最新的 rc.2。若把 latest 标签直接交给 npm，就会得到"dsh CLI 停在旧版、
//! 所有插件包升到新版"的版本偏斜，运行时插件随即加载失败。

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use serde::Serialize;
use serde_json::Value;

use crate::paths;
use crate::version::{compare_versions, is_exact_version};

const PACKAGE_NAME: &str = "@deepseek-ai/dsh";

/// 安装失败时回传给用户界面的日志尾部行数。
const ERROR_TAIL_LINES: usize = 20;

/// npm 全局安装在 prefix 根目录生成的转发脚本（内容为相对路径，可整体搬移）。
const ROOT_ENTRIES: &[&str] = &["node_modules", "dsh", "dsh.cmd", "dsh.ps1", "dsh.sh"];

const STAGING_PREFIX: &str = ".staging-";
const BACKUP_PREFIX: &str = ".backup-";

const NPM_TIMEOUT: Duration = Duration::from_secs(15 * 60);

#[derive(Debug, Clone, Serialize)]
pub struct UpgradeResult {
    pub success: bool,
    pub cancelled: bool,
    pub output: String,
}

impl UpgradeResult {
    fn ok(output: impl Into<String>) -> Self {
        Self { success: true, cancelled: false, output: output.into() }
    }
    fn fail(output: impl Into<String>) -> Self {
        Self { success: false, cancelled: false, output: output.into() }
    }
    fn cancel(output: impl Into<String>) -> Self {
        Self { success: false, cancelled: true, output: output.into() }
    }
}

/// 从某源的 dist-tags 解析"应当安装的确切版本"：取 latest 与 next 中较新者。
pub fn resolve_version(dist_tags: &Value) -> Option<String> {
    let mut best: Option<String> = None;
    for tag in ["latest", "next"] {
        let candidate = dist_tags.get(tag).and_then(Value::as_str);
        if let Some(candidate) = candidate {
            if !is_exact_version(candidate) {
                continue;
            }
            let take = best
                .as_deref()
                .map(|b| compare_versions(candidate, b) == std::cmp::Ordering::Greater)
                .unwrap_or(true);
            if take {
                best = Some(candidate.to_string());
            }
        }
    }
    best
}

/// 把 dsh 安装 / 升级到安装根目录。`version` 必须是确切版本号，不接受 latest 标签。
/// `progress` 逐行接收 npm 输出与阶段说明。
pub async fn install(
    version: &str,
    registry_url: &str,
    progress: impl Fn(String) + Send + Sync + 'static,
) -> UpgradeResult {
    if !is_exact_version(version) {
        return UpgradeResult::fail(format!(
            "内部错误：安装 dsh 必须指定确切版本号，收到 \"{version}\"。\n\
             直接使用 latest / next 这类标签会导致 dsh CLI 与插件包版本不一致，安装必然损坏。"
        ));
    }

    let node = paths::bundled_node();
    let npm_cli = paths::bundled_npm_cli();
    if !node.is_file() || !npm_cli.is_file() {
        return UpgradeResult::fail("运行时缺少 node/npm（安装包不完整），无法安装或升级 dsh。");
    }
    let install_root = paths::install_root();
    let staging_root = install_root.join(format!(
        "{STAGING_PREFIX}{}",
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis())
            .unwrap_or(0)
    ));
    let progress: Arc<dyn Fn(String) + Send + Sync> = Arc::new(progress);

    let result = install_inner(&node, &npm_cli, &install_root, &staging_root, version, registry_url, &progress).await;

    // 无论成败清掉暂存目录（成功后其内容已被搬走，只剩空壳）
    try_delete_any(&staging_root);
    result
}

async fn install_inner(
    node: &Path,
    npm_cli: &Path,
    install_root: &Path,
    staging_root: &Path,
    version: &str,
    registry_url: &str,
    progress: &Arc<dyn Fn(String) + Send + Sync>,
) -> UpgradeResult {
    if let Err(e) = std::fs::create_dir_all(staging_root) {
        return UpgradeResult::fail(format!("无法创建暂存目录 {}：{e}", staging_root.display()));
    }
    progress(format!("[准备] 暂存目录中完整安装 dsh@{version}（避免就地更新的残留文件）"));

    let npm_output = run_npm_install(node, npm_cli, staging_root, version, registry_url, progress).await;
    match &npm_output {
        NpmOutcome::Cancelled(msg) => return UpgradeResult::cancel(msg.clone()),
        NpmOutcome::Failed(_) => {
            return UpgradeResult::fail(format!(
                "npm 安装失败：\n{}",
                tail(&npm_output_text(&npm_output), ERROR_TAIL_LINES)
            ));
        }
        NpmOutcome::Succeeded(_) => {}
    }

    // 校验：必须与期望版本一致，且不存在比 CLI 更新的插件包（即版本偏斜）
    if let Err(problem) = verify_tree(staging_root, version) {
        return UpgradeResult::fail(format!(
            "安装结果校验未通过，已保留原有安装：\n{problem}\n\n{}",
            tail(&npm_output_text(&npm_output), ERROR_TAIL_LINES)
        ));
    }

    progress("[切换] 校验通过，正在替换原有安装…".to_string());
    if let Err(problem) = commit_staged_install(install_root, staging_root) {
        return UpgradeResult::fail(format!("替换安装目录失败，原有安装已保留：\n{problem}"));
    }

    // 安装目录已被整体替换：清除模块回退缓存，让 dsh 下次启动依据新安装重建。
    match reset_profile_module_fallback() {
        Ok(()) => progress("[清理] 已重置模块回退缓存，dsh 将在启动时重建。".to_string()),
        Err(e) => progress(format!("[清理] 重置模块回退缓存失败（启动自检会重试）：{e}")),
    }

    progress(format!("[完成] dsh 已更新到 {version}"));
    UpgradeResult::ok(format!("dsh 已更新到 {version}。"))
}

enum NpmOutcome {
    Succeeded(String),
    Failed(String),
    Cancelled(String),
}

fn npm_output_text(outcome: &NpmOutcome) -> String {
    match outcome {
        NpmOutcome::Succeeded(t) | NpmOutcome::Failed(t) | NpmOutcome::Cancelled(t) => t.clone(),
    }
}

/// 用捆绑 npm 在指定 prefix 下安装 dsh 的确切版本，实时回调输出行。
async fn run_npm_install(
    node: &Path,
    npm_cli: &Path,
    prefix_root: &Path,
    version: &str,
    registry_url: &str,
    progress: &Arc<dyn Fn(String) + Send + Sync>,
) -> NpmOutcome {
    use tokio::io::AsyncBufReadExt;

    let mut cmd = tokio::process::Command::new(node);
    cmd.arg(npm_cli)
        .arg("install")
        .arg("-g")
        .arg("--prefix")
        .arg(prefix_root)
        .arg("--registry")
        .arg(registry_url)
        .arg("--omit=dev")
        .arg("--no-audit")
        .arg("--no-fund")
        // dsh 的部分依赖需要执行安装脚本（原生模块），这里显式放行
        .arg("--allow-scripts=@deepseek-ai/dsh-subprocess-local,koffi,node-pty,@google/genai,protobufjs")
        // 传确切版本，绝不用 latest/next 标签
        .arg(format!("{PACKAGE_NAME}@{version}"))
        .current_dir(prefix_root)
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped())
        .stdin(std::process::Stdio::null())
        .kill_on_drop(true);
    crate::host::apply_platform_process_setup(&mut cmd);

    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => return NpmOutcome::Failed(format!("无法启动 npm 安装进程：{e}")),
    };

    let mut stdout = child.stdout.take().expect("stdout piped");
    let mut stderr = child.stderr.take().expect("stderr piped");

    let mut all_output: Vec<String> = Vec::new();
    let timed_out = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
    let timed_out_flag = timed_out.clone();

    let pump_progress = progress.clone();
    let pump_out = async {
        let mut lines = tokio::io::BufReader::new(&mut stdout).lines();
        while let Ok(Some(line)) = lines.next_line().await {
            pump_progress(line.clone());
            all_output.push(line);
        }
    };
    let pump_progress_err = progress.clone();
    let pump_err = async {
        let mut lines = tokio::io::BufReader::new(&mut stderr).lines();
        while let Ok(Some(line)) = lines.next_line().await {
            pump_progress_err(line);
        }
    };

    let wait = async {
        match tokio::time::timeout(NPM_TIMEOUT, child.wait()).await {
            Ok(Ok(status)) => status.success(),
            Ok(Err(_)) => false,
            // 超时：kill_on_drop(true) 会在 child 被 drop 时终止进程
            Err(_) => {
                timed_out_flag.store(true, std::sync::atomic::Ordering::SeqCst);
                false
            }
        }
    };

    let (success, _, _) = tokio::join!(wait, pump_out, pump_err);

    if !success {
        let text = all_output.join("\n");
        if timed_out.load(std::sync::atomic::Ordering::SeqCst) {
            return NpmOutcome::Cancelled(format!("安装超时（15 分钟），已中止。\n{}", tail(&text, ERROR_TAIL_LINES)));
        }
        return NpmOutcome::Failed(text);
    }
    NpmOutcome::Succeeded(all_output.join("\n"))
}

/// 体检"自动安装目录"中的 dsh：未安装视为健康；已安装则要求版本自洽，
/// 否则说明是"CLI 旧、插件新"的偏斜安装，需要重装修复。
pub fn is_managed_install_healthy() -> Result<(), String> {
    if !paths::is_dsh_installed() {
        return Ok(());
    }
    let cli_version = read_version(&paths::dsh_package_json())
        .ok_or_else(|| "无法读取已安装 dsh 的版本号，安装文件可能已损坏。".to_string())?;
    verify_tree(&paths::install_root(), &cli_version)
}

/// 校验某个 prefix 下的安装是否完整且版本自洽：
/// 1. 入口 lib/bin.js 存在；2. CLI 版本等于期望版本；3. 没有任何 dsh-* 插件包版本高于 CLI。
pub fn verify_tree(prefix_root: &Path, expected_version: &str) -> Result<(), String> {
    let dsh_dir = prefix_root
        .join("node_modules")
        .join("@deepseek-ai")
        .join("dsh");
    let bin_script = dsh_dir.join("lib").join("bin.js");
    if !bin_script.is_file() {
        return Err("未找到 dsh 入口文件 node_modules/@deepseek-ai/dsh/lib/bin.js，安装不完整。".into());
    }

    let cli_version = read_version(&dsh_dir.join("package.json"))
        .ok_or_else(|| "无法读取 dsh 的 package.json 版本号。".to_string())?;
    if compare_versions(&cli_version, expected_version) != std::cmp::Ordering::Equal {
        return Err(format!(
            "dsh CLI 实际版本为 {cli_version}，与期望的 {expected_version} 不一致。"
        ));
    }

    // 版本偏斜检测：插件包比 CLI 新，就是"CLI 没换掉、插件却被升级"的坏状态
    for pkg_json in enumerate_dsh_plugin_packages(prefix_root) {
        if let Some(v) = read_version(&pkg_json) {
            if compare_versions(&v, &cli_version) == std::cmp::Ordering::Greater {
                return Err(format!(
                    "检测到版本偏斜：dsh CLI 为 {cli_version}，但插件包已是 {v}。这会导致运行时插件加载失败。"
                ));
            }
        }
    }
    Ok(())
}

/// 枚举安装树中所有 @deepseek-ai/dsh-* 插件包的 package.json 路径。
/// npm 全局安装会把依赖嵌套在包自身的 node_modules 下，两处都要看。
fn enumerate_dsh_plugin_packages(prefix_root: &Path) -> Vec<PathBuf> {
    let mut result = Vec::new();
    let scopes = [
        prefix_root.join("node_modules").join("@deepseek-ai"),
        prefix_root
            .join("node_modules")
            .join("@deepseek-ai")
            .join("dsh")
            .join("node_modules")
            .join("@deepseek-ai"),
    ];
    for scope in scopes {
        let Ok(entries) = std::fs::read_dir(&scope) else {
            continue;
        };
        for entry in entries.flatten() {
            let name = entry.file_name();
            let name = name.to_string_lossy();
            if name.to_lowercase().starts_with("dsh-") {
                result.push(entry.path().join("package.json"));
            }
        }
    }
    result
}

fn read_version(package_json_path: &Path) -> Option<String> {
    let bytes = std::fs::read(package_json_path).ok()?;
    let value: Value = serde_json::from_slice(&bytes).ok()?;
    value.get("version").and_then(Value::as_str).map(String::from)
}

/// 把暂存目录中的 node_modules 与转发脚本整体替换到安装根目录：
/// 旧内容先移入备份目录，替换成功再删除备份；中途失败则回滚，绝不留半新半旧的状态。
fn commit_staged_install(install_root: &Path, staging_root: &Path) -> Result<(), String> {
    let staged_modules = staging_root.join("node_modules");
    if !staged_modules.is_dir() {
        return Err("暂存目录中没有生成 node_modules，安装未完成。".into());
    }

    let backup_root = install_root.join(format!(
        "{BACKUP_PREFIX}{}",
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis())
            .unwrap_or(0)
    ));
    std::fs::create_dir_all(&backup_root)
        .map_err(|e| format!("无法创建备份目录：{e}"))?;

    let mut backed_up: Vec<(PathBuf, PathBuf)> = Vec::new();
    let mut installed: Vec<PathBuf> = Vec::new();

    macro_rules! rollback {
        () => {{
            for path in &installed {
                try_delete_any(path);
            }
            for (live, backup) in &backed_up {
                let _ = move_with_retry(backup, live);
            }
            try_delete_any(&backup_root);
        }};
    }

    // 1) 旧内容挪到备份
    for name in ROOT_ENTRIES {
        let live = install_root.join(name);
        if !live.exists() {
            continue;
        }
        let dest = backup_root.join(name);
        if let Err(e) = move_with_retry(&live, &dest) {
            let problem = e;
            rollback!();
            return Err(problem);
        }
        backed_up.push((live, dest));
    }

    // 2) 新内容搬到安装根
    for name in ROOT_ENTRIES {
        let src = staging_root.join(name);
        if !src.exists() {
            continue;
        }
        let dest = install_root.join(name);
        if let Err(e) = move_with_retry(&src, &dest) {
            let problem = e;
            rollback!();
            return Err(problem);
        }
        installed.push(dest);
    }

    // 备份已无用处；即使删不掉（句柄未释放）也不影响使用
    try_delete_any(&backup_root);
    Ok(())
}

/// 同卷内的移动（目录用原子重命名）；Windows 上句柄释放有延迟，遇占用则重试。
fn move_with_retry(from: &Path, to: &Path) -> Result<(), String> {
    const MAX_ATTEMPTS: usize = 8;
    for attempt in 1..=MAX_ATTEMPTS {
        let result = if from.is_dir() {
            std::fs::rename(from, to)
        } else {
            std::fs::rename(from, to).or_else(|_| std::fs::copy(from, to).map(|_| ()))
        };
        match result {
            Ok(()) => return Ok(()),
            Err(e) if attempt < MAX_ATTEMPTS => {
                let _ = &e;
                std::thread::sleep(Duration::from_millis(250 * attempt as u64));
            }
            Err(e) => return Err(format!("移动 {} → {} 失败：{e}", from.display(), to.display())),
        }
    }
    Ok(())
}

/// 清理安装根下遗留的暂存 / 备份目录（上次异常退出可能残留）。
pub fn cleanup_leftovers() {
    let root = paths::install_root();
    let Ok(entries) = std::fs::read_dir(&root) else {
        return;
    };
    for entry in entries.flatten() {
        let name = entry.file_name();
        let name = name.to_string_lossy();
        if name.starts_with(STAGING_PREFIX) || name.starts_with(BACKUP_PREFIX) {
            try_delete_any(&entry.path());
        }
    }
}

// ── 模块回退缓存（$DSH_HOME/profiles/node_modules）────────
//
// dsh 并不把 in-box bundles（@deepseek-ai/dsh-base、dsh-web-app 及其 dsh-client-ui-* 依赖）
// 装进 profile 目录，而是用 $DSH_HOME/profiles/node_modules 这个"符号链接农场"把
// 安装目录的依赖闭包暴露给 profile —— Node 解析裸包名时会向上走到 profiles/node_modules。
//
// 一旦安装目录被整体替换（升级 / 重装）而这个缓存没被重建，链接就会缺失或悬空，
// profile 随即解析不到这些包，启动时报：
//   Cannot find package '@deepseek-ai/dsh-client-ui-...' imported from ...\.dsh\profiles\web\
// 这正是"更新后必须删光所有文件重装"的真正原因。
//
// 该缓存是纯派生数据：删掉后 dsh 下次启动会依据当前安装重建，因此这里用"检测 + 清除"
// 代替代价高昂的重装。

/// 校验模块回退缓存是否与当前安装匹配。缓存或 profile 目录尚不存在时视为健康。
pub fn is_profile_module_fallback_healthy() -> Result<(), String> {
    let fallback = paths::profile_module_fallback_dir();
    if !fallback.is_dir() {
        return Ok(()); // 还没生成过，dsh 启动时会建
    }
    let scope_dir = paths::installed_scope_dir();
    if !scope_dir.is_dir() {
        return Ok(());
    }

    let fallback_scope = fallback.join("@deepseek-ai");

    // 1) 安装目录里有的包，缓存里必须能解析到（对"缺失"和"符号链接悬空"，
    //    exists()/is_dir() 都返回 false，两种情况都说明该重建）
    if let Ok(entries) = std::fs::read_dir(&scope_dir) {
        for entry in entries.flatten() {
            let name = entry.file_name();
            if !fallback_scope.join(&name).is_dir() {
                return Err(format!("模块回退缓存缺少或已失效的条目：{}", name.to_string_lossy()));
            }
        }
    }

    // 2) 缓存里指向已不存在目标的残留条目（包已被新版本移除）同样要重建
    if fallback_scope.is_dir() {
        for entry in std::fs::read_dir(&fallback_scope)
            .map_err(|e| e.to_string())?
            .flatten()
        {
            let path = entry.path();
            if !path.exists() {
                return Err(format!(
                    "模块回退缓存存在失效条目：{}",
                    path.file_name().unwrap_or_default().to_string_lossy()
                ));
            }
        }
    }
    Ok(())
}

/// 清除模块回退缓存，迫使 dsh 在下次启动时依据当前安装重新生成。
/// 若缓存本就不存在则视为成功。
pub fn reset_profile_module_fallback() -> Result<(), String> {
    let fallback = paths::profile_module_fallback_dir();
    if !fallback.is_dir() {
        return Ok(());
    }
    delete_tree_without_following_links(&fallback)
}

/// 删除回退缓存目录树。
///
/// **刻意不做递归删除跟进符号链接：** 该目录里是成百上千个符号链接，一旦递归删除
/// 跟进链接，就会删到链接指向的真实安装目录（数据丢失）。这里逐项删除链接本身，
/// 再删空目录。目录结构固定为两层（@scope/pkg 或 pkg）。
fn delete_tree_without_following_links(root: &Path) -> Result<(), String> {
    let entries = std::fs::read_dir(root).map_err(|e| e.to_string())?;
    for entry in entries.flatten() {
        let path = entry.path();
        let is_link = std::fs::symlink_metadata(&path)
            .map(|m| m.file_type().is_symlink())
            .unwrap_or(false);
        if is_link || path.is_file() {
            delete_link_or_file(&path);
            continue;
        }
        // 真实目录（只可能是 @scope 作用域目录），其子项同样只删链接本身
        if let Ok(children) = std::fs::read_dir(&path) {
            for child in children.flatten() {
                let child_path = child.path();
                let child_is_link = std::fs::symlink_metadata(&child_path)
                    .map(|m| m.file_type().is_symlink())
                    .unwrap_or(false);
                if child_is_link || child_path.is_file() {
                    delete_link_or_file(&child_path);
                } else {
                    let _ = std::fs::remove_dir(&child_path);
                }
            }
        }
        let _ = std::fs::remove_dir(&path);
    }
    std::fs::remove_dir(root).map_err(|e| e.to_string())
}

/// 删除链接或文件本身（非递归），绝不进入链接目标。
fn delete_link_or_file(path: &Path) {
    if std::fs::remove_dir(path).is_ok() {
        return; // 目录型链接 / 空目录
    }
    let _ = std::fs::remove_file(path);
}

// ── 小工具 ──────────────────────────────────────────────

fn tail(text: &str, lines: usize) -> String {
    if text.trim().is_empty() {
        return "（无输出）".into();
    }
    let all: Vec<&str> = text.split('\n').collect();
    if all.len() <= lines {
        text.to_string()
    } else {
        all[all.len() - lines..].join("\n")
    }
}

fn try_delete_any(path: &Path) {
    if path.is_dir() {
        let _ = std::fs::remove_dir_all(path);
    } else if path.is_file() {
        let _ = std::fs::remove_file(path);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn resolve_version_prefers_newer_tag() {
        let tags = json!({"latest": "0.1.5-rc.1", "next": "0.1.5-rc.2"});
        assert_eq!(resolve_version(&tags).as_deref(), Some("0.1.5-rc.2"));

        let tags = json!({"latest": "1.2.3", "next": "1.2.2"});
        assert_eq!(resolve_version(&tags).as_deref(), Some("1.2.3"));

        let tags = json!({"latest": "latest-alias"});
        assert_eq!(resolve_version(&tags), None);
    }

    #[test]
    fn verify_tree_rejects_missing_bin() {
        let dir = std::env::temp_dir().join(format!("dsh-verify-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let prefix = dir.join("prefix");
        std::fs::create_dir_all(&prefix).unwrap();
        assert!(verify_tree(&prefix, "1.0.0").is_err());
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn verify_tree_detects_skew() {
        let dir = std::env::temp_dir().join(format!("dsh-skew-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let dsh_dir = dir
            .join("node_modules")
            .join("@deepseek-ai")
            .join("dsh")
            .join("lib");
        let plugin_dir = dir
            .join("node_modules")
            .join("@deepseek-ai")
            .join("dsh-subprocess-local");
        std::fs::create_dir_all(&dsh_dir).unwrap();
        std::fs::create_dir_all(&plugin_dir).unwrap();
        std::fs::write(
            dsh_dir.join("bin.js"),
            "// entry\n",
        )
        .unwrap();
        std::fs::write(
            dsh_dir.parent().unwrap().join("package.json"),
            r#"{"version":"1.0.0"}"#,
        )
        .unwrap();
        std::fs::write(plugin_dir.join("package.json"), r#"{"version":"2.0.0"}"#).unwrap();

        // CLI 1.0.0、插件 2.0.0 → 版本偏斜
        assert!(verify_tree(&dir, "1.0.0").is_err());
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn verify_tree_accepts_consistent() {
        let dir = std::env::temp_dir().join(format!("dsh-ok-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let dsh_dir = dir
            .join("node_modules")
            .join("@deepseek-ai")
            .join("dsh")
            .join("lib");
        let plugin_dir = dir
            .join("node_modules")
            .join("@deepseek-ai")
            .join("dsh")
            .join("node_modules")
            .join("@deepseek-ai")
            .join("dsh-base");
        std::fs::create_dir_all(&dsh_dir).unwrap();
        std::fs::create_dir_all(&plugin_dir).unwrap();
        std::fs::write(dsh_dir.join("bin.js"), "// entry\n").unwrap();
        std::fs::write(
            dsh_dir.parent().unwrap().join("package.json"),
            r#"{"version":"1.0.0"}"#,
        )
        .unwrap();
        std::fs::write(plugin_dir.join("package.json"), r#"{"version":"1.0.0"}"#).unwrap();
        assert!(verify_tree(&dir, "1.0.0").is_ok());
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn tail_works() {
        assert_eq!(tail("", 5), "（无输出）");
        assert_eq!(tail("a\nb\nc", 5), "a\nb\nc");
        assert_eq!(tail("a\nb\nc\nd", 2), "c\nd");
    }

    #[test]
    fn fallback_cache_delete_does_not_follow_links() {
        // 构造：真实目录 + 指向它的符号链接（unix 才能建符号链接；Windows 跳过）
        if cfg!(windows) {
            return;
        }
        let base = std::env::temp_dir().join(format!("dsh-fallback-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&base);
        let real = base.join("real-pkg");
        std::fs::create_dir_all(&real).unwrap();
        std::fs::write(real.join("data.txt"), "keep me").unwrap();
        let cache = base.join("cache");
        let scope = cache.join("@scope");
        std::fs::create_dir_all(&scope).unwrap();
        #[cfg(unix)]
        std::os::unix::fs::symlink(&real, scope.join("real-pkg")).unwrap();

        delete_tree_without_following_links(&cache).unwrap();
        // 链接已删，但真实内容仍在
        assert!(real.join("data.txt").is_file());
        let _ = std::fs::remove_dir_all(&base);
    }
}

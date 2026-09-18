//! 应用状态机（C# 版 MainWindow 启动/安装/升级/崩溃自愈逻辑的移植）。

use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use serde::Serialize;
use tauri::{AppHandle, Emitter, Manager, Url};

use crate::host::{CrashInfo, HostCallbacks, HostProcess};
use crate::installer;
use crate::locator::{self, DshRuntime};
use crate::paths;
use crate::plugins;
use crate::registry;
use crate::settings::AppSettings;
use crate::version as vermod;

const MAX_LOG_LINES: usize = 300;
const START_TIMEOUT_SECS: u64 = 45;

/// 主页面（index.html）所处阶段，驱动状态覆盖层显示。
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Phase {
    Idle,
    Installing,
    Starting,
    Running,
    Exited,
    Error,
    Timeout,
}

#[derive(Clone, Serialize)]
pub struct StatusPayload {
    pub phase: Phase,
    pub title: String,
    pub detail: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub code: Option<i32>,
}

pub struct AppState {
    pub settings: Mutex<AppSettings>,
    pub host: Arc<HostProcess>,
    pub current_url: Mutex<Option<String>>,
    /// 主 WebView 当前是否在 dsh 页面（true）还是本地 index 页（false）。
    pub navigated: AtomicBool,
    pub phase: Mutex<Phase>,
    pub installing: AtomicBool,
    pub updating: AtomicBool,
    pub handling_crash: AtomicBool,
    pub log: Mutex<VecDeque<String>>,
    /// 每次启动自增，用于让过期的 45s 超时任务失效。
    pub startup_generation: AtomicU64,
    /// 串行化启动流程，避免重复触发（tokio 锁：guard 需要跨 await 持有）。
    pub startup_lock: tokio::sync::Mutex<()>,
}

impl AppState {
    pub fn new(settings: AppSettings) -> Self {
        Self {
            settings: Mutex::new(settings),
            host: Arc::new(HostProcess::new()),
            current_url: Mutex::new(None),
            navigated: AtomicBool::new(false),
            phase: Mutex::new(Phase::Idle),
            installing: AtomicBool::new(false),
            updating: AtomicBool::new(false),
            handling_crash: AtomicBool::new(false),
            log: Mutex::new(VecDeque::new()),
            startup_generation: AtomicU64::new(0),
            startup_lock: tokio::sync::Mutex::new(()),
        }
    }

    pub fn append_log(&self, line: &str) {
        if line.is_empty() {
            return;
        }
        let mut log = self.log.lock().unwrap();
        log.push_back(line.to_string());
        if log.len() > MAX_LOG_LINES {
            let overflow = log.len() - MAX_LOG_LINES;
            log.drain(..overflow);
        }
    }

    pub fn log_snapshot(&self) -> Vec<String> {
        self.log.lock().unwrap().iter().cloned().collect()
    }

    pub fn set_phase(&self, phase: Phase) {
        *self.phase.lock().unwrap() = phase;
    }

    pub fn current_phase(&self) -> Phase {
        self.phase.lock().unwrap().clone()
    }
}

// ── 事件辅助 ────────────────────────────────────────────

pub fn emit_log(app: &AppHandle, line: &str) {
    let _ = app.emit("log", serde_json::json!({ "line": line }));
}

pub fn emit_status(app: &AppHandle, payload: &StatusPayload) {
    let _ = app.emit("status", payload);
}

fn set_phase_and_emit(app: &AppHandle, state: &AppState, phase: Phase, title: &str, detail: &str) {
    state.set_phase(phase.clone());
    let payload = StatusPayload {
        phase,
        title: title.to_string(),
        detail: detail.to_string(),
        code: None,
    };
    // 主窗口若在 dsh 页面上，需要拉回本地状态页展示
    if state.navigated.load(Ordering::SeqCst) {
        navigate_main_home(app);
        state.navigated.store(false, Ordering::SeqCst);
    }
    emit_status(app, &payload);
}

fn navigate_main_home(app: &AppHandle) {
    if let Some(main) = app.get_webview_window("main") {
        let index = index_url(&main);
        let _ = main.navigate(index);
    }
}

pub fn index_url(webview: &tauri::WebviewWindow) -> Url {
    // 与 Tauri 本地资源协议保持一致的绝对地址
    let _ = webview;
    if cfg!(windows) {
        Url::parse("http://tauri.localhost/index.html").expect("valid index url")
    } else {
        Url::parse("tauri://localhost/index.html").expect("valid index url")
    }
}

// ── 宿主回调 ────────────────────────────────────────────

fn build_host_callbacks(app: AppHandle, state: Arc<AppState>) -> HostCallbacks {
    HostCallbacks {
        url_ready: {
            let app = app.clone();
            let state = state.clone();
            Arc::new(move |url| {
                *state.current_url.lock().unwrap() = Some(url.clone());
                state.set_phase(Phase::Running);
                state.navigated.store(true, Ordering::SeqCst);
                state.startup_generation.fetch_add(1, Ordering::SeqCst); // 使超时任务失效
                if let Some(main) = app.get_webview_window("main") {
                    if let Ok(parsed) = Url::parse(&url) {
                        let _ = main.navigate(parsed);
                    }
                    let _ = main.set_title(&format!("DeepSeek Harness — 服务运行中"));
                }
                let _ = app.emit("service-url", serde_json::json!({ "url": url }));
            })
        },
        output: {
            let app = app.clone();
            let state = state.clone();
            Arc::new(move |line| {
                state.append_log(&line);
                emit_log(&app, &line);
            })
        },
        error: {
            let app = app.clone();
            let state = state.clone();
            Arc::new(move |line| {
                state.append_log(&line);
                emit_log(&app, &line);
            })
        },
        exited: {
            let app = app.clone();
            let state = state.clone();
            Arc::new(move |code| {
                set_phase_and_emit(
                    &app,
                    &state,
                    Phase::Exited,
                    "DeepSeek Harness 服务已退出",
                    &format!("退出码 {code}。可点击「重新加载」重启服务。"),
                );
            })
        },
        crashed: {
            let app = app.clone();
            let state = state.clone();
            Arc::new(move |crash| handle_crash(&app, &state, crash))
        },
    }
}

// ── 启动流程（RunStartupAsync 移植）────────────────────

/// 统一的启动入口：定位 dsh（必要时自动联网安装最新版），然后启动服务。
pub async fn run_startup(app: AppHandle, state: Arc<AppState>) {
    let _guard = match state.startup_lock.try_lock() {
        Ok(g) => g,
        Err(_) => return, // 已有启动流程在跑
    };    state.startup_generation.fetch_add(1, Ordering::SeqCst);

    // 清掉上次异常退出可能残留的 .staging-* / .backup-* 目录
    installer::cleanup_leftovers();

    let dsh_path = state.settings.lock().unwrap().dsh_path.clone();
    let mut runtime = locator::find(&dsh_path);

    if runtime.is_none() {
        // 用户手动指定了路径但找不到 → 直接报错，不自动覆盖用户意图
        if !dsh_path.trim().is_empty() {
            set_phase_and_emit(
                &app,
                &state,
                Phase::Error,
                "未检测到 DeepSeek Harness CLI",
                &format!(
                    "设置中指定的 dsh 路径不可用：{dsh_path}\n请打开设置修正路径，或清空后使用自动安装。"
                ),
            );
            return;
        }
        // 未指定路径且本机没有 dsh → 自动联网安装最新版
        if !auto_install_dsh(&app, &state, false).await {
            return; // 失败提示已在安装流程中给出
        }
        runtime = locator::find(&dsh_path);
        if runtime.is_none() {
            set_phase_and_emit(
                &app,
                &state,
                Phase::Error,
                "自动安装失败",
                "dsh 已安装但无法定位，请点击「重新加载」重试，或检查安装目录权限。",
            );
            return;
        }
    }

    let runtime = runtime.expect("runtime checked above");

    // 自检并自愈（一）：自动安装目录里的 dsh 若体检发现损坏（典型症状是
    // "CLI 旧、插件新"的版本偏斜），直接重装成版本一致的安装。
    let from_managed_install = runtime.script_path == paths::dsh_bin_script().to_string_lossy();
    if from_managed_install {
        if let Err(problem) = installer::is_managed_install_healthy() {
            state.append_log(&format!("[自检] 检测到 dsh 安装异常：{problem}"));
            emit_log(&app, &format!("[自检] 检测到 dsh 安装异常：{problem}"));
            emit_log(&app, "[自检] 正在重新安装以修复…");
            if !auto_install_dsh(&app, &state, true).await {
                return;
            }
            let Some(rt) = locator::find(&dsh_path) else {
                set_phase_and_emit(
                    &app,
                    &state,
                    Phase::Error,
                    "修复失败",
                    "重装后仍无法定位 dsh，请点击「重新加载」重试，或检查安装目录权限。",
                );
                return;
            };
            return start_host(&app, &state, rt).await;
        }

        // 自检并自愈（二）：模块回退缓存（$DSH_HOME/profiles/node_modules）若缺少条目
        // 或符号链接悬空，profile 就解析不到 @deepseek-ai/dsh-*，启动即报错。
        if let Err(problem) = installer::is_profile_module_fallback_healthy() {
            emit_log(&app, &format!("[自检] 检测到模块回退缓存异常：{problem}"));
            match installer::reset_profile_module_fallback() {
                Ok(()) => emit_log(&app, "[自检] 已清除回退缓存，dsh 启动时会自动重建。"),
                Err(e) => emit_log(&app, &format!("[自检] 清除回退缓存失败：{e}")),
            }
        }
    }

    start_host(&app, &state, runtime).await;
}

async fn start_host(app: &AppHandle, state: &Arc<AppState>, runtime: DshRuntime) {
    let display = runtime.display_name();
    set_phase_and_emit(app, state, Phase::Starting, "正在启动 DeepSeek Harness 服务…", &display);
    if let Err(e) = state.host.start(&runtime, build_host_callbacks(app.clone(), state.clone())) {
        set_phase_and_emit(app, state, Phase::Error, "服务启动失败", &e);
        return;
    }

    // 启动 45 秒超时：若服务未就绪（如插件加载挂起），提示用户而不是无限等待
    let app = app.clone();
    let state = state.clone();
    let generation = state.startup_generation.load(Ordering::SeqCst);
    tauri::async_runtime::spawn(async move {
        tokio::time::sleep(std::time::Duration::from_secs(START_TIMEOUT_SECS)).await;
        if state.startup_generation.load(Ordering::SeqCst) != generation {
            return; // 已就绪或已重启
        }
        if state.navigated.load(Ordering::SeqCst) {
            return;
        }
        set_phase_and_emit(
            &app,
            &state,
            Phase::Timeout,
            "服务启动超时",
            &format!(
                "dsh 服务未在 {START_TIMEOUT_SECS} 秒内就绪，可能是插件不兼容导致加载挂起。\n\
                 可打开「插件管理」在「已屏蔽」中处理，或点击重新加载重试。"
            ),
        );
    });
}

/// 安装 dsh。`repair` = true 用于重装修复损坏的安装（版本偏斜等）。
/// 返回是否成功（失败提示已在流程中给出）。
pub async fn auto_install_dsh(app: &AppHandle, state: &Arc<AppState>, repair: bool) -> bool {
    if state.installing.load(Ordering::SeqCst) {
        return false;
    }
    state.installing.store(true, Ordering::SeqCst);
    let action = if repair { "修复" } else { "自动安装" };
    let result = auto_install_inner(app, state, action).await;
    state.installing.store(false, Ordering::SeqCst);
    result
}

async fn auto_install_inner(app: &AppHandle, state: &Arc<AppState>, action: &str) -> bool {
    if !paths::is_bundled_runtime_complete() {
        set_phase_and_emit(
            app,
            state,
            Phase::Error,
            &format!("{action}不可用"),
            "安装包缺少捆绑的 Node/npm 运行时，无法配置 dsh。\n请重新安装本应用，或在设置中手动指定 dsh 路径。",
        );
        return false;
    }

    state.set_phase(Phase::Installing);
    let (title, detail) = if action == "修复" {
        (
            "正在重新安装 DeepSeek Harness (dsh) 以修复损坏的安装…".to_string(),
            format!("安装目录：{}\n联网下载可能需要几分钟，进度见日志。", paths::install_root().display()),
        )
    } else {
        (
            "首次使用：正在自动安装 DeepSeek Harness (dsh)…".to_string(),
            format!("安装目录：{}\n联网下载可能需要几分钟，进度见日志。", paths::install_root().display()),
        )
    };
    set_phase_and_emit(app, state, Phase::Installing, &title, &detail);

    let log_line = format!("[dsh] 开始{action}到 {}", paths::install_root().display());
    state.append_log(&log_line);
    emit_log(app, &log_line);
    emit_log(app, "[dsh] 正在探测最快的 npm 源（官方 + 国内镜像）…");

    let Some(registry) = registry::select_best_registry().await else {
        set_phase_and_emit(
            app,
            state,
            Phase::Error,
            &format!("{action}失败"),
            "无法连接任何 npm 源（官方与国内镜像均不可达）。\n请检查网络后点击「重新加载」重试，或在设置中手动指定 dsh。",
        );
        return false;
    };

    // 关键：从 dist-tags 解析出"确切版本号"再安装，绝不能把 latest 标签交给 npm。
    let Some(version) = installer::resolve_version(&registry.dist_tags) else {
        set_phase_and_emit(
            app,
            state,
            Phase::Error,
            &format!("{action}失败"),
            &format!(
                "更新源 {} 上没有可用的 dsh 版本。\n请稍后重试，或在设置中手动指定 dsh 路径。",
                registry.name
            ),
        );
        return false;
    };
    emit_log(app, &format!("[dsh] 已选择更新源：{}（{}ms）", registry.name, registry.latency_ms));
    emit_log(app, &format!("[dsh] 开始安装 @deepseek-ai/dsh@{version} …"));

    let app_for_progress = app.clone();
    let result = installer::install(&version, &registry.url, move |line| {
        emit_log(&app_for_progress, &line);
    })
    .await;

    if result.cancelled {
        set_phase_and_emit(
            app,
            state,
            Phase::Error,
            &format!("{action}已取消"),
            "可在网络就绪后点击「重新加载」重试。",
        );
        return false;
    }
    if !result.success {
        emit_log(app, &result.output);
        set_phase_and_emit(
            app,
            state,
            Phase::Error,
            &format!("{action}失败"),
            "npm 安装未成功，详情见日志。\n可点击「重新加载」重试，或在设置中手动指定 dsh 路径。",
        );
        return false;
    }

    emit_log(app, &format!("[dsh] {action}完成：{}", local_version().unwrap_or_else(|| "已安装".into())));
    true
}

pub fn local_version() -> Option<String> {
    let bytes = std::fs::read(paths::dsh_package_json()).ok()?;
    let value: serde_json::Value = serde_json::from_slice(&bytes).ok()?;
    value.get("version").and_then(serde_json::Value::as_str).map(String::from)
}

// ── 崩溃自愈（OnCrash 移植）───────────────────────────

fn handle_crash(app: &AppHandle, state: &Arc<AppState>, crash: CrashInfo) {
    if state.handling_crash.swap(true, Ordering::SeqCst) {
        return;
    }
    let app = app.clone();
    let state = state.clone();
    tauri::async_runtime::spawn(async move {
        crash_flow(&app, &state, crash).await;
        state.handling_crash.store(false, Ordering::SeqCst);
    });
}

async fn crash_flow(app: &AppHandle, state: &Arc<AppState>, crash: CrashInfo) {
    // 崩溃恢复路径自身绝不能再让异常逃逸
    let mut disabled: Vec<String> = Vec::new();

    // 1. 自动屏蔽（卸载 + 记录）报错插件。
    //    注意：不能在持有 settings 锁的状态下 await（std 锁不可跨 await），
    //    因此每次先卸载、成功后再短暂加锁记录。
    for pkg in &crash.plugin_names {
        let removed = plugins::remove(pkg).await;
        if removed.success {
            let mut settings = state.settings.lock().unwrap();
            settings.disable(pkg);
            let _ = settings.save();
            if !disabled.contains(pkg) {
                disabled.push(pkg.clone());
            }
        }
    }

    // 2. 以安全配置自动重启（已排除报错插件）
    restart_service(app, state).await;

    let _ = app.emit(
        "crash",
        serde_json::json!({
            "names": crash.plugin_names,
            "disabled": disabled,
            "log": crash.error_log,
        }),
    );

    // 3. 弹窗告知用户（插件名 + 报错日志摘要）
    let mut text = format!("以下插件加载失败导致服务退出：{}", crash.plugin_names.join(", "));
    if !disabled.is_empty() {
        text.push_str(&format!(
            "\n\n已自动屏蔽并卸载：{}。服务已尝试以安全配置重启。",
            disabled.join(", ")
        ));
    }
    text.push_str("\n\n报错日志（末尾部分）：\n");
    let all_lines: Vec<&str> = crash.error_log.lines().collect();
    let start = all_lines.len().saturating_sub(20);
    text.push_str(&all_lines[start..].join("\n"));
    text.push_str("\n\n如需恢复该插件，请打开「插件管理」在「已屏蔽」中选择「恢复」。");

    use tauri_plugin_dialog::{DialogExt, MessageDialogButtons, MessageDialogKind};
    app.dialog()
        .message(text)
        .title("插件加载失败（已自动屏蔽）")
        .kind(MessageDialogKind::Warning)
        .buttons(MessageDialogButtons::Ok)
        .show(|_| {});
}

// ── 更新流程（CheckForUpdate / UpgradeDsh 移植）────────

/// 手动检查 dsh 新版本：有新版 → 弹窗询问升级；无新版 / 无网络 → 弹窗提示。
/// 返回给菜单调用的异步任务。
pub async fn check_for_update_flow(app: AppHandle, state: Arc<AppState>) {
    if state.updating.load(Ordering::SeqCst) {
        return;
    }

    // 尚未安装 dsh：手动检查无意义，改为引导自动安装
    if local_version().is_none() {
        let confirmed = confirm_dialog(
            &app,
            "尚未安装 dsh",
            &format!(
                "检测到本机尚未安装 DeepSeek Harness (dsh)。\n是否立即联网安装最新版到：\n{}",
                paths::install_root().display()
            ),
            "立即安装",
            "取消",
        )
        .await;
        if confirmed {
            restart_service(&app, &state).await; // 内部：找不到 dsh → 自动安装
        }
        return;
    }

    let Some(registry) = registry::select_best_registry().await else {
        message_dialog(&app, "检查更新失败", "无法连接任何 npm 源（官方与国内镜像均不可达），请检查网络后重试。");
        return;
    };
    let remote = installer::resolve_version(&registry.dist_tags);
    let local = local_version().unwrap_or_else(|| "0.0.0".into());

    let Some(latest) = remote else {
        message_dialog(&app, "检查更新失败", &format!("更新源 {} 上没有可用的 dsh 版本。", registry.name));
        return;
    };

    if vermod::compare_versions(&latest, &local) != std::cmp::Ordering::Greater {
        message_dialog(
            &app,
            "已是最新版本",
            &format!("当前 dsh 版本：{local}\n更新源：{}（{}ms）", registry.name, registry.latency_ms),
        );
        return;
    }

    let confirmed = confirm_dialog(
        &app,
        "发现新版本",
        &format!(
            "检测到 DeepSeek Harness (dsh) 新版本：\n{local} → {latest}\n\n\
             升级需要联网下载，完成后会自动重启服务。是否现在升级？\n更新源：{}（{}ms）",
            registry.name, registry.latency_ms
        ),
        "立即升级",
        "稍后再说",
    )
    .await;
    if confirmed {
        upgrade_dsh(&app, &state, &latest, &registry.url, &registry.name).await;
    }
}

/// 升级 dsh：停止服务 → 暂存安装 → 校验替换 → （可选）刷新插件树 → 重启。
pub async fn upgrade_dsh(app: &AppHandle, state: &Arc<AppState>, version: &str, registry_url: &str, registry_name: &str) {
    if state.updating.swap(true, Ordering::SeqCst) {
        return;
    }

    let ver = version.to_string();
    let url = registry_url.to_string();
    let name = registry_name.to_string();
    let local = local_version().unwrap_or_else(|| "?".into());

    set_phase_and_emit(app, state, Phase::Installing, "正在升级 dsh…", &format!("{local} → {ver}\n更新源：{name}"));
    emit_log(app, &format!("[升级] {local} → {ver}（源：{name}）"));

    // 停止服务并等待进程树完全退出：npm 替换文件时若 dsh 仍在运行，
    // 会因文件被占用而跳过，从而留下"CLI 旧、插件新"的坏安装。
    stop_host_blocking(state).await;
    state.navigated.store(false, Ordering::SeqCst);
    *state.current_url.lock().unwrap() = None;
    navigate_main_home(app);

    let app_for_progress = app.clone();
    let result = installer::install(&ver, &url, move |line| {
        emit_log(&app_for_progress, &line);
    })
    .await;

    if result.cancelled {
        message_dialog(app, "升级已取消", &result.output);
        restart_service(app, state).await;
        state.updating.store(false, Ordering::SeqCst);
        return;
    }
    if !result.success {
        message_dialog(app, "dsh 升级失败", &if result.output.trim().is_empty() { "未知错误".into() } else { result.output });
        restart_service(app, state).await;
        state.updating.store(false, Ordering::SeqCst);
        return;
    }

    // 升级成功后、重启服务前，刷新 profile 的插件树（设置项默认开启）
    let mut profile_note = String::new();
    if state.settings.lock().unwrap().refresh_profile_after_update {
        set_phase_and_emit(app, state, Phase::Installing, "正在刷新插件树…", "在 profile 目录执行 pnpm update，可能需要几分钟…");
        let refresh = plugins::refresh_profile().await;
        if refresh.success {
            profile_note = "\n插件树已刷新。".into();
        } else {
            emit_log(app, &format!("[插件树] 刷新失败：{}", refresh.output));
            profile_note = "\n提示：插件树刷新失败（不影响 dsh 本体），可在「插件管理」中重试。".into();
        }
    }

    restart_service(app, state).await;
    message_dialog(app, "升级完成", &format!("dsh 已成功升级到 {ver}。{profile_note}"));
    state.updating.store(false, Ordering::SeqCst);
}

// ── 服务重启 ───────────────────────────────────────────

pub async fn stop_host_blocking(state: &Arc<AppState>) {
    let host = state.host.clone();
    let _ = tauri::async_runtime::spawn_blocking(move || host.stop()).await;
}

/// 重启服务：等待进程树真正退出后再重启，避免残留进程占用端口 / 文件。
pub async fn restart_service(app: &AppHandle, state: &Arc<AppState>) {
    stop_host_blocking(state).await;
    state.navigated.store(false, Ordering::SeqCst);
    *state.current_url.lock().unwrap() = None;
    navigate_main_home(app);
    run_startup(app.clone(), state.clone()).await;
}

fn message_dialog(app: &AppHandle, title: &str, text: &str) {
    use tauri_plugin_dialog::{DialogExt, MessageDialogButtons, MessageDialogKind};
    app.dialog()
        .message(text.to_string())
        .title(title.to_string())
        .kind(MessageDialogKind::Info)
        .buttons(MessageDialogButtons::Ok)
        .show(|_| {});
}

async fn confirm_dialog(app: &AppHandle, title: &str, text: &str, yes: &str, no: &str) -> bool {
    use tauri_plugin_dialog::{DialogExt, MessageDialogButtons, MessageDialogKind};
    let (yes, no) = (yes.to_string(), no.to_string());
    let (tx, rx) = tokio::sync::oneshot::channel::<bool>();
    app.dialog()
        .message(text.to_string())
        .title(title.to_string())
        .kind(MessageDialogKind::Info)
        .buttons(MessageDialogButtons::OkCancelCustom(yes, no))
        .show(move |confirmed| {
            let _ = tx.send(confirmed);
        });
    rx.await.unwrap_or(false)
}

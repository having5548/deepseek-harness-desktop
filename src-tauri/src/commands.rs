//! Tauri 命令层：前端页面（index/log/plugins/settings）通过 invoke 调用。

use std::sync::atomic::Ordering;
use std::sync::Arc;

use serde::Serialize;
use tauri::{AppHandle, State};

use crate::plugins::{self, PluginEntry};
use crate::settings::AppSettings;
use crate::state::{self, AppState, Phase};

#[derive(Serialize)]
pub struct StatusSnapshot {
    pub phase: Phase,
    pub service_url: Option<String>,
    pub navigated: bool,
    pub log: Vec<String>,
    pub dsh_version: Option<String>,
    pub install_root: String,
    pub running: bool,
    pub app_version: String,
}

#[tauri::command]
pub fn get_status(state: State<'_, Arc<AppState>>) -> StatusSnapshot {
    let state = state.inner();
    StatusSnapshot {
        phase: state.current_phase(),
        service_url: state.current_url.lock().unwrap().clone(),
        navigated: state.navigated.load(Ordering::SeqCst),
        log: state.log_snapshot(),
        dsh_version: state::local_version(),
        install_root: crate::paths::install_root().to_string_lossy().into_owned(),
        running: state.host.is_running(),
        app_version: env!("CARGO_PKG_VERSION").to_string(),
    }
}

#[tauri::command]
pub fn clear_log(state: State<'_, Arc<AppState>>) {
    state.log.lock().unwrap().clear();
}

/// 前端页面就绪后触发一次启动（重复触发会被串行化 + 运行检测挡住）。
#[tauri::command]
pub async fn startup(app: AppHandle, state: State<'_, Arc<AppState>>) -> Result<(), String> {
    let state = state.inner().clone();
    state::run_startup(app, state).await;
    Ok(())
}

#[tauri::command]
pub async fn restart_service(app: AppHandle, state: State<'_, Arc<AppState>>) -> Result<(), String> {
    let state = state.inner().clone();
    state::restart_service(&app, &state).await;
    Ok(())
}

#[tauri::command]
pub fn get_settings(state: State<'_, Arc<AppState>>) -> AppSettings {
    state.settings.lock().unwrap().clone()
}

#[tauri::command]
pub fn save_settings(
    app: AppHandle,
    state: State<'_, Arc<AppState>>,
    settings: AppSettings,
) -> Result<(), String> {
    settings.save().map_err(|e| e.to_string())?;
    *state.settings.lock().unwrap() = settings;
    let state = state.inner().clone();
    // 保存后重启服务以应用新的 dsh 路径等设置（与 C# 版一致）
    tauri::async_runtime::spawn(async move {
        state::restart_service(&app, &state).await;
    });
    Ok(())
}

/// 仅持久化设置，不重启服务（插件来源切换等轻量变更用）。
#[tauri::command]
pub fn save_settings_quiet(state: State<'_, Arc<AppState>>, settings: AppSettings) -> Result<(), String> {
    settings.save().map_err(|e| e.to_string())?;
    *state.settings.lock().unwrap() = settings;
    Ok(())
}

// ── 插件 ───────────────────────────────────────────────

#[tauri::command]
pub fn plugins_installed() -> Vec<String> {
    plugins::get_installed_plugins()
}

#[tauri::command]
pub fn builtin_bundles() -> Vec<String> {
    plugins::BUILTIN_BUNDLES.iter().map(|s| s.to_string()).collect()
}

#[tauri::command]
pub fn plugin_sources() -> Vec<plugins::PluginSource> {
    plugins::all_sources()
}

#[derive(Serialize)]
pub struct PluginsFetchResponse {
    pub plugins: Vec<PluginEntry>,
    pub failed_sources: Vec<String>,
    pub source_names: Vec<String>,
    pub from_cache: bool,
    pub cache_saved_at: String,
}

/// 按设置解析生效来源 → 拉取 → 去重合并 → 写缓存；全失败时回退本地缓存。
#[tauri::command]
pub async fn plugins_fetch(state: State<'_, Arc<AppState>>) -> Result<PluginsFetchResponse, String> {
    let settings = state.settings.lock().unwrap().clone();
    let sources = resolve_active_sources(&settings);
    let source_names: Vec<String> = sources.iter().map(|s| s.name.to_string()).collect();
    let result = plugins::fetch(&sources).await;
    let merged = plugins::merge_and_dedupe(result.plugins);

    if !merged.is_empty() {
        let cache = crate::plugins::PluginCacheFile {
            sources: source_names.clone(),
            plugins: merged.clone(),
            ..Default::default()
        };
        plugins::save_cache(&cache);
        return Ok(PluginsFetchResponse {
            plugins: merged,
            failed_sources: result.failed_sources,
            source_names,
            from_cache: false,
            cache_saved_at: String::new(),
        });
    }

    // 所有来源不可用 → 回退缓存
    if let Some(cache) = plugins::load_cache() {
        let saved_at = cache.saved_at.clone();
        let cache_sources = cache.sources.clone();
        Ok(PluginsFetchResponse {
            plugins: cache.plugins,
            failed_sources: result.failed_sources,
            source_names: cache_sources,
            from_cache: true,
            cache_saved_at: saved_at,
        })
    } else {
        Ok(PluginsFetchResponse {
            plugins: Vec::new(),
            failed_sources: result.failed_sources,
            source_names,
            from_cache: false,
            cache_saved_at: String::new(),
        })
    }
}

fn resolve_active_sources(settings: &AppSettings) -> Vec<plugins::PluginSource> {
    let all = plugins::all_sources();
    if settings.plugin_source_mode == "single" {
        let selected = plugins::find_source(&settings.selected_plugin_source)
            .unwrap_or_else(|| all[0].clone());
        return vec![selected];
    }
    let enabled: Vec<plugins::PluginSource> = all
        .into_iter()
        .filter(|s| settings.enabled_plugin_sources.iter().any(|id| id == s.id))
        .collect();
    if enabled.is_empty() {
        vec![plugins::find_source("dsh-market").expect("dsh-market source exists")]
    } else {
        enabled
    }
}

/// 安装插件；成功后清除屏蔽记录并自动重启服务（与 C# 版行为一致）。
#[tauri::command]
pub async fn plugin_install(
    app: AppHandle,
    state: State<'_, Arc<AppState>>,
    install_spec: String,
    package_name: String,
) -> Result<plugins::PluginCommandResult, String> {
    let state = state.inner().clone();
    let result = plugins::install(&install_spec).await;
    if result.success {
        {
            let mut settings = state.settings.lock().unwrap();
            settings.enable(&package_name);
            let _ = settings.save();
        }
        state::restart_service(&app, &state).await;
    }
    Ok(result)
}

#[tauri::command]
pub async fn plugin_remove(
    app: AppHandle,
    state: State<'_, Arc<AppState>>,
    package_name: String,
) -> Result<plugins::PluginCommandResult, String> {
    let state = state.inner().clone();
    let result = plugins::remove(&package_name).await;
    if result.success {
        state::restart_service(&app, &state).await;
    }
    Ok(result)
}

/// 恢复被屏蔽的插件：重新安装并移除屏蔽记录。
#[tauri::command]
pub async fn plugin_restore(
    app: AppHandle,
    state: State<'_, Arc<AppState>>,
    package_name: String,
) -> Result<plugins::PluginCommandResult, String> {
    let state = state.inner().clone();
    let result = plugins::install(&package_name).await;
    if result.success {
        {
            let mut settings = state.settings.lock().unwrap();
            settings.enable(&package_name);
            let _ = settings.save();
        }
        state::restart_service(&app, &state).await;
    }
    Ok(result)
}

/// 手动刷新插件树（等价 profile 目录 pnpm update）。
#[tauri::command]
pub async fn plugin_refresh_profile() -> plugins::PluginCommandResult {
    plugins::refresh_profile().await
}

// ── 其他 ───────────────────────────────────────────────

#[tauri::command]
pub fn open_external(app: AppHandle, url: String) {
    use tauri_plugin_opener::OpenerExt;
    let _ = app.opener().open_url(url, None::<&str>);
}

/// 设置页的「浏览…」文件选择器。
#[tauri::command]
pub async fn pick_dsh_file(app: AppHandle) -> Option<String> {
    use tauri_plugin_dialog::{DialogExt, FilePath};
    let (tx, rx) = tokio::sync::oneshot::channel::<Option<String>>();
    app.dialog()
        .file()
        .add_filter("dsh 可执行文件", &["cmd", "exe", "js", "sh"])
        .pick_file(move |file| {
            let text = file.and_then(|f| match f {
                FilePath::Path(p) => Some(p.to_string_lossy().into_owned()),
                _ => None,
            });
            let _ = tx.send(text);
        });
    rx.await.ok().flatten()
}

#[derive(Serialize)]
pub struct UpdateInfo {
    pub local_version: Option<String>,
    pub latest_version: Option<String>,
    pub registry_name: Option<String>,
    pub latency_ms: Option<u64>,
}

/// 供设置页显示版本检查结果（完整升级交互由菜单「检查更新」驱动）。
#[tauri::command]
pub async fn check_update_info(state: State<'_, Arc<AppState>>) -> Result<UpdateInfo, String> {
    let _ = state;
    let local = state::local_version();
    let registry = crate::registry::select_best_registry().await;
    Ok(UpdateInfo {
        latest_version: registry
            .as_ref()
            .and_then(|r| crate::installer::resolve_version(&r.dist_tags)),
        registry_name: registry.as_ref().map(|r| r.name.clone()),
        latency_ms: registry.as_ref().map(|r| r.latency_ms),
        local_version: local,
    })
}

/// 状态页「在系统浏览器中打开」。
#[tauri::command]
pub fn open_service_in_browser(app: AppHandle, state: State<'_, Arc<AppState>>) {
    if let Some(url) = state.current_url.lock().unwrap().clone() {
        open_external(app, url);
    }
}

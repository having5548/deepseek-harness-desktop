//! DeepSeek Harness 桌面客户端（Rust / Tauri 版）
//!
//! 原生窗口直接导航到 dsh web 服务地址（鉴权 cookie 依赖顶级导航上下文），
//! 顶部工具栏为注入到页面里的悬浮胶囊（见 toolbar.js）；状态/日志/插件/设置各为独立本地页面窗口。

// 无条件 GUI 子系统：任何构建（含 debug）都不带控制台窗口，
// 前台永远只有应用主窗口。调试输出走应用内「启动日志」窗口，不依赖终端。
#![windows_subsystem = "windows"]

mod commands;
mod host;
mod installer;
mod locator;
mod paths;
mod plugins;
mod registry;
mod settings;
mod state;
mod version;

use std::sync::Arc;

use tauri::webview::{NewWindowFeatures, NewWindowResponse};
use tauri::{Emitter, Manager, Url, WindowEvent};
use tauri_plugin_opener::OpenerExt;

use state::AppState;

fn main() {
    let settings = settings::AppSettings::load();

    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            // 二次启动：拉起已有主窗口
            if let Some(main) = app.get_webview_window("main") {
                let _ = main.unminimize();
                let _ = main.show();
                let _ = main.set_focus();
            }
        }))
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_dialog::init())
        .manage(Arc::new(AppState::new(settings)))
        .invoke_handler(tauri::generate_handler![
            commands::get_status,
            commands::clear_log,
            commands::startup,
            commands::restart_service,
            commands::get_settings,
            commands::save_settings,
            commands::save_settings_quiet,
            commands::plugins_installed,
            commands::builtin_bundles,
            commands::plugin_sources,
            commands::plugins_fetch,
            commands::plugin_install,
            commands::plugin_remove,
            commands::plugin_restore,
            commands::plugin_refresh_profile,
            commands::open_external,
            commands::open_service_in_browser,
            commands::pick_dsh_file,
            commands::check_update_info,
        ])
        .setup(|app| {
            build_main_window(app.handle())?;
            Ok(())
        })
        .on_window_event(|window, event| {
            if window.label() == "main" {
                if let WindowEvent::CloseRequested { .. } = event {
                    // 退出应用时终止整棵 dsh 子进程树，绝不残留。
                    // 必须同步清理：若放到后台线程，主进程可能在 taskkill 完成前就退出，
                    // 子进程树会残留（实测踩过的坑）。
                    let state: tauri::State<Arc<AppState>> = window.app_handle().state();
                    state.host.stop();
                }
            }
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

/// 主窗口：先加载本地状态页；dsh 服务就绪后由核心导航到服务地址。
fn build_main_window(app: &tauri::AppHandle) -> tauri::Result<()> {
    let builder = tauri::WebviewWindowBuilder::new(app, "main", tauri::WebviewUrl::App("index.html".into()))
        .title("DeepSeek Harness")
        .inner_size(1280.0, 800.0)
        .min_inner_size(860.0, 560.0)
        // 注入式悬浮工具栏（应用自己的顶栏）：原生菜单栏已移除，详见 toolbar.js 顶部注释
        .initialization_script(include_str!("toolbar.js"));
    attach_link_handlers(builder, app.clone()).build()?;
    Ok(())
}

/// 统一决定"链接去哪里"：
/// - 应用自身的本地页面、以及本机回环地址上的 dsh 服务 → 允许在窗口内导航；
/// - 其他 http(s)（含 `target="_blank"` / `window.open` 弹出的新窗口）→ 交给系统浏览器，
///   同时阻止在应用内导航或弹出新窗口。
///
/// 这是 C# 版 `CoreWebView2.NewWindowRequested` + 外链拦截的等价实现。
/// 此前 Rust 版缺少这层处理：dsh 页面里 `target="_blank"` 的链接点了没有任何反应
/// —— 既不会调起系统浏览器，也不会开新窗口，看起来就是"链接点不动"。
fn attach_link_handlers(
    builder: tauri::WebviewWindowBuilder<'_, tauri::Wry, tauri::AppHandle>,
    app: tauri::AppHandle,
) -> tauri::WebviewWindowBuilder<'_, tauri::Wry, tauri::AppHandle> {
    let nav_app = app.clone();
    let win_app = app;
    builder
        .on_navigation(move |url| {
            if let Some(action) = toolbar_action_for(url) {
                dispatch_toolbar_action(&nav_app, &action);
                return false;
            }
            if is_local_page(url) {
                return true;
            }
            open_in_system_browser(&nav_app, url);
            false
        })
        .on_new_window(move |url, _features: NewWindowFeatures| {
            if let Some(action) = toolbar_action_for(&url) {
                dispatch_toolbar_action(&win_app, &action);
                return NewWindowResponse::Deny;
            }
            if is_local_page(&url) {
                // 目标仍是本地页面：回到主窗口打开，避免多出一个窗口
                let app = win_app.clone();
                let target = url.clone();
                std::thread::spawn(move || {
                    if let Some(main) = app.get_webview_window("main") {
                        let _ = main.navigate(target);
                    }
                });
            } else {
                open_in_system_browser(&win_app, &url);
            }
            NewWindowResponse::Deny
        })
}

/// 应用自身的本地页面（Tauri 资源协议）或本机 dsh 服务地址。
fn is_local_page(url: &Url) -> bool {
    match url.scheme() {
        "tauri" | "about" | "data" => true,
        "http" | "https" => {
            let host = url.host_str().unwrap_or_default();
            host.eq_ignore_ascii_case("tauri.localhost")
                || host.eq_ignore_ascii_case("localhost")
                || host == "127.0.0.1"
                || host == "::1"
        }
        _ => false,
    }
}

/// 用系统默认浏览器 / 协议处理器打开链接（失败只记日志，绝不打断用户操作）。
fn open_in_system_browser(app: &tauri::AppHandle, url: &Url) {
    if let Err(e) = app.opener().open_url(url.to_string(), None::<&str>) {
        log::warn!("无法打开链接 {url}：{e}");
        let _ = app.emit(
            "log",
            serde_json::json!({ "line": format!("[界面] 无法打开链接 {url}：{e}") }),
        );
    }
}

/// 注入式工具栏与 Rust 侧的通信主机名。
///
/// 远端页面（dsh 服务页）拿不到 Tauri IPC —— capability 只授权本地来源、没有 `remote`
/// 条目。所以 toolbar.js 的按钮改用 `window.open("https://dsh-desktop.invalid/<action>")`
/// 发请求，由下面的 [`toolbar_action_for`] 翻译成真正的动作。这样既不必给远端来源开 IPC
/// 权限，也不会真的弹出窗口。`.invalid` 是 RFC 2606 保留后缀，永不会被解析到真实站点。
const ACTION_HOST: &str = "dsh-desktop.invalid";

/// 从哨兵地址中解析出工具栏动作名；非哨兵地址返回 `None`。
fn toolbar_action_for(url: &Url) -> Option<String> {
    let is_action_host = url
        .host_str()
        .map(|h| h.eq_ignore_ascii_case(ACTION_HOST))
        .unwrap_or(false);
    if !is_action_host {
        return None;
    }
    let action = url.path().trim_matches('/').to_string();
    (!action.is_empty()).then_some(action)
}

/// 执行工具栏按钮请求的动作。
///
/// 在独立线程上执行：本函数由 `on_navigation` / `on_new_window` 回调调用，而这两个回调
/// 位于 webview 事件线程上；若在其中同步创建窗口（`open_helper_window`），会与该线程
/// 互相等待而死锁。因此这里立刻返回，实际工作交给新线程。
fn dispatch_toolbar_action(app: &tauri::AppHandle, action: &str) {
    let app = app.clone();
    let action = action.to_string();
    std::thread::spawn(move || {
        let state: Arc<AppState> = app.state::<Arc<AppState>>().inner().clone();
        match action.as_str() {
            "reconnect" => {
                let handle = app.clone();
                tauri::async_runtime::spawn(async move {
                    state::restart_service(&handle, &state).await;
                });
            }
            "external" => {
                if let Some(url) = state.current_url.lock().unwrap().clone() {
                    if let Ok(parsed) = Url::parse(&url) {
                        open_in_system_browser(&app, &parsed);
                    }
                }
            }
            "plugins" => open_helper_window(
                &app,
                "plugins",
                "plugins.html",
                "插件管理 — DeepSeek Harness",
                720.0,
                640.0,
            ),
            "settings" => open_helper_window(
                &app,
                "settings",
                "settings.html",
                "设置 — DeepSeek Harness",
                560.0,
                520.0,
            ),
            "logs" => open_helper_window(
                &app,
                "logs",
                "log.html",
                "启动日志 — DeepSeek Harness",
                760.0,
                560.0,
            ),
            "update" => {
                let handle = app.clone();
                tauri::async_runtime::spawn(async move {
                    state::check_for_update_flow(handle, state).await;
                });
            }
            _ => {}
        }
    });
}

fn open_helper_window(app: &tauri::AppHandle, label: &str, url: &str, title: &str, w: f64, h: f64) {
    if let Some(existing) = app.get_webview_window(label) {
        let _ = existing.unminimize();
        let _ = existing.show();
        let _ = existing.set_focus();
        return;
    }
    let result = attach_link_handlers(
        tauri::WebviewWindowBuilder::new(app, label, tauri::WebviewUrl::App(url.into()))
            .title(title)
            .inner_size(w, h)
            .min_inner_size(420.0, 320.0),
        app.clone(),
    )
    .build();
    if let Err(e) = result {
        log::error!("无法打开窗口 {label}: {e}");
        let _ = app.emit("log", serde_json::json!({ "line": format!("[界面] 无法打开窗口 {label}: {e}") }));
    }
}

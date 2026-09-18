//! DeepSeek Harness 桌面客户端（Rust / Tauri 版）
//!
//! 原生窗口直接导航到 dsh web 服务地址（鉴权 cookie 依赖顶级导航上下文），
//! 工具栏功能由原生菜单提供；状态/日志/插件/设置各为独立本地页面窗口。

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

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

use std::sync::atomic::Ordering;
use std::sync::Arc;

use tauri::menu::{MenuBuilder, MenuItem, SubmenuBuilder};
use tauri::{Emitter, Manager, WindowEvent};
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
            build_menu(app.handle())?;
            Ok(())
        })
        .on_menu_event(|app, event| {
            handle_menu_event(app, event.id().as_ref());
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

fn build_menu(app: &tauri::AppHandle) -> tauri::Result<()> {
    let nav_back = MenuItem::with_id(app, "nav_back", "后退", true, Some("Alt+Left"))?;
    let nav_forward = MenuItem::with_id(app, "nav_forward", "前进", true, Some("Alt+Right"))?;
    let nav_reload = MenuItem::with_id(app, "nav_reload", "重新加载", true, Some("Ctrl+R"))?;
    let nav_home = MenuItem::with_id(app, "nav_home", "重新连接服务", true, Some("Ctrl+Shift+H"))?;
    let nav_external = MenuItem::with_id(app, "nav_external", "在系统浏览器中打开", true, Some("Ctrl+Shift+O"))?;

    let tool_update = MenuItem::with_id(app, "tool_update", "检查更新…", true, None::<&str>)?;
    let tool_plugins = MenuItem::with_id(app, "tool_plugins", "插件管理…", true, Some("Ctrl+Shift+P"))?;
    let tool_settings = MenuItem::with_id(app, "tool_settings", "设置…", true, Some("Ctrl+Comma"))?;
    let tool_logs = MenuItem::with_id(app, "tool_logs", "启动日志", true, Some("Ctrl+L"))?;

    let nav_submenu = SubmenuBuilder::new(app, "导航")
        .item(&nav_back)
        .item(&nav_forward)
        .item(&nav_reload)
        .item(&nav_home)
        .separator()
        .item(&nav_external)
        .build()?;
    let tool_submenu = SubmenuBuilder::new(app, "工具")
        .item(&tool_update)
        .item(&tool_plugins)
        .item(&tool_settings)
        .separator()
        .item(&tool_logs)
        .build()?;

    let menu = MenuBuilder::new(app)
        .item(&nav_submenu)
        .item(&tool_submenu)
        .build()?;

    app.set_menu(menu)?;
    Ok(())
}

fn handle_menu_event(app: &tauri::AppHandle, id: &str) {
    let state: tauri::State<Arc<AppState>> = app.state();
    let state = state.inner().clone();
    match id {
        "nav_back" | "nav_forward" | "nav_reload" => {
            let navigated = state.navigated.load(Ordering::SeqCst);
            if navigated {
                if let Some(main) = app.get_webview_window("main") {
                    let js = match id {
                        "nav_back" => "history.back()",
                        "nav_forward" => "history.forward()",
                        _ => "location.reload()",
                    };
                    let _ = main.eval(js);
                }
            } else if id == "nav_reload" {
                let app = app.clone();
                tauri::async_runtime::spawn(async move {
                    state::restart_service(&app, &state).await;
                });
            }
        }
        "nav_home" => {
            if let Some(url) = state.current_url.lock().unwrap().clone() {
                if let Ok(parsed) = tauri::Url::parse(&url) {
                    if let Some(main) = app.get_webview_window("main") {
                        let _ = main.navigate(parsed);
                        state.navigated.store(true, Ordering::SeqCst);
                    }
                }
            }
        }
        "nav_external" => {
            if let Some(url) = state.current_url.lock().unwrap().clone() {
                let _ = app.opener().open_url(url, None::<&str>);
            }
        }
        "tool_update" => {
            let app = app.clone();
            tauri::async_runtime::spawn(async move {
                state::check_for_update_flow(app, state).await;
            });
        }
        "tool_plugins" => open_helper_window(app, "plugins", "plugins.html", "插件管理 — DeepSeek Harness", 720.0, 640.0),
        "tool_settings" => open_helper_window(app, "settings", "settings.html", "设置 — DeepSeek Harness", 560.0, 520.0),
        "tool_logs" => open_helper_window(app, "logs", "log.html", "启动日志 — DeepSeek Harness", 760.0, 560.0),
        _ => {}
    }
}

fn open_helper_window(app: &tauri::AppHandle, label: &str, url: &str, title: &str, w: f64, h: f64) {
    if let Some(existing) = app.get_webview_window(label) {
        let _ = existing.unminimize();
        let _ = existing.show();
        let _ = existing.set_focus();
        return;
    }
    let result = tauri::WebviewWindowBuilder::new(app, label, tauri::WebviewUrl::App(url.into()))
        .title(title)
        .inner_size(w, h)
        .min_inner_size(420.0, 320.0)
        .build();
    if let Err(e) = result {
        log::error!("无法打开窗口 {label}: {e}");
        let _ = app.emit("log", serde_json::json!({ "line": format!("[界面] 无法打开窗口 {label}: {e}") }));
    }
}

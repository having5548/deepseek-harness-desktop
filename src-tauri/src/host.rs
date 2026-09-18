//! 管理 dsh web 服务子进程（C# 版 DshHostProcess 移植）：
//! 以 `--profile web --no-open --port 0` 启动，解析
//! `dsh web: http://127.0.0.1:<port>` 输出行得到真实 URL（含鉴权 token），
//! 检测插件加载失败导致的崩溃，并负责退出时终止整棵进程树。

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use tokio::io::AsyncBufReadExt;

use crate::locator::DshRuntime;

const MAX_ERROR_BUFFER: usize = 200;
const URL_MARKER: &str = "dsh web: http://127.0.0.1:";

/// 服务因插件加载失败而崩溃的信息。
#[derive(Debug, Clone, serde::Serialize)]
pub struct CrashInfo {
    pub plugin_names: Vec<String>,
    pub error_log: String,
}

/// 服务进程事件回调集合。
#[derive(Clone)]
pub struct HostCallbacks {
    /// 服务已就绪，参数为可访问的 Web UI URL。
    pub url_ready: Arc<dyn Fn(String) + Send + Sync>,
    /// 服务标准输出（每行）。
    pub output: Arc<dyn Fn(String) + Send + Sync>,
    /// 服务错误输出（每行）。
    pub error: Arc<dyn Fn(String) + Send + Sync>,
    /// 服务进程退出，参数为退出码。
    pub exited: Arc<dyn Fn(i32) + Send + Sync>,
    /// 检测到插件加载失败导致的崩溃。
    pub crashed: Arc<dyn Fn(CrashInfo) + Send + Sync>,
}

/// dsh web 服务进程句柄。Stop/Start 生命周期与 C# 版一致：
/// Stop 后允许干净地再次 Start。
pub struct HostProcess {
    pid: Arc<Mutex<Option<u32>>>,
    stopping: Arc<AtomicBool>,
    reaped: Arc<AtomicBool>,
    error_buffer: Arc<Mutex<Vec<String>>>,
}

impl HostProcess {
    pub fn new() -> Self {
        Self {
            pid: Arc::new(Mutex::new(None)),
            stopping: Arc::new(AtomicBool::new(false)),
            reaped: Arc::new(AtomicBool::new(true)),
            error_buffer: Arc::new(Mutex::new(Vec::new())),
        }
    }

    pub fn is_running(&self) -> bool {
        self.pid.lock().unwrap().is_some()
    }

    /// 启动 dsh web。以用户主目录为工作目录，便于 dsh 读取用户级的 .env（如 DEEPSEEK_API_KEY）。
    pub fn start(&self, runtime: &DshRuntime, callbacks: HostCallbacks) -> Result<(), String> {
        if self.is_running() {
            return Ok(());
        }

        let mut cmd = tokio::process::Command::new(&runtime.node_path);
        if !runtime.script_path.is_empty() {
            cmd.arg(&runtime.script_path);
        }
        cmd.args(["--profile", "web", "--no-open", "--port", "0"])
            .current_dir(crate::paths::user_home_dir())
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::piped())
            .stdin(std::process::Stdio::null())
            .kill_on_drop(true);

        // 注入 PATH：`dsh plugin` 内部 spawnSync('pnpm') 依赖 PATH 找到捆绑的 pnpm。
        // unix 下 npm -g --prefix 的 shim 位于 runtime/bin/，也要加入。
        if let Some(dir) = std::path::Path::new(&runtime.node_path).parent() {
            let existing = std::env::var("PATH").unwrap_or_default();
            let mut prefix = format!("{}{}", dir.to_string_lossy(), path_separator());
            if !cfg!(windows) {
                prefix.push_str(&format!("{}{}{}", dir.join("bin").to_string_lossy(), path_separator(), ""));
            }
            cmd.env("PATH", format!("{prefix}{existing}"));
        }
        apply_platform_process_setup(&mut cmd);

        let mut child = cmd
            .spawn()
            .map_err(|e| format!("无法启动 dsh 服务进程：{e}"))?;
        let pid = child
            .id()
            .ok_or_else(|| "dsh 服务进程启动即退出".to_string())?;
        *self.pid.lock().unwrap() = Some(pid);
        self.stopping.store(false, Ordering::SeqCst);
        self.reaped.store(false, Ordering::SeqCst);
        self.error_buffer.lock().unwrap().clear();

        let mut stdout = child.stdout.take().expect("stdout piped");
        let mut stderr = child.stderr.take().expect("stderr piped");

        // 泵标准输出 + 提取鉴权 URL
        {
            let cb = callbacks.clone();
            tauri::async_runtime::spawn(async move {
                let mut lines = tokio::io::BufReader::new(&mut stdout).lines();
                while let Ok(Some(line)) = lines.next_line().await {
                    // dsh 0.1.2+ 的 web URL 携带鉴权 token，例如
                    //   dsh web: http://127.0.0.1:56137/?token=xxxx
                    // 必须捕获完整 URL（含 query），否则 WebView 打开无 token 的地址会被拒绝访问。
                    if let Some(idx) = line.find(URL_MARKER) {
                        let url = line[idx + "dsh web: ".len()..].trim().to_string();
                        (cb.url_ready)(url);
                    }
                    (cb.output)(line);
                }
            });
        }

        // 泵标准错误 + 环形缓冲（供崩溃检测）
        {
            let cb = callbacks.clone();
            let buffer = self.error_buffer.clone();
            tauri::async_runtime::spawn(async move {
                let mut lines = tokio::io::BufReader::new(&mut stderr).lines();
                while let Ok(Some(line)) = lines.next_line().await {
                    (cb.error)(line.clone());
                    let mut guard = buffer.lock().unwrap();
                    guard.push(line);
                    if guard.len() > MAX_ERROR_BUFFER {
                        let overflow = guard.len() - MAX_ERROR_BUFFER;
                        guard.drain(..overflow);
                    }
                }
            });
        }

        // 退出监视：child 所有权归本任务
        {
            let pid_slot = self.pid.clone();
            let stopping = self.stopping.clone();
            let reaped = self.reaped.clone();
            let buffer = self.error_buffer.clone();
            let cb = callbacks.clone();
            tauri::async_runtime::spawn(async move {
                let status = child.wait().await;
                reaped.store(true, Ordering::SeqCst);
                *pid_slot.lock().unwrap() = None;

                // 主动停止（升级 / 重启）不是异常退出，不要上报，否则界面会闪出"服务已退出"错误
                if stopping.load(Ordering::SeqCst) {
                    return;
                }
                let code = status.map(|s| s.code().unwrap_or(-1)).unwrap_or(-1);
                (cb.exited)(code);

                let crash = try_detect_crash(&buffer.lock().unwrap());
                if let Some(crash) = crash {
                    (cb.crashed)(crash);
                }
            });
        }
        Ok(())
    }

    /// 终止服务进程及其整棵子进程树，并等待退出监视任务收尾。
    /// 结束后允许干净地再次 start。
    pub fn stop(&self) {
        let pid = self.pid.lock().unwrap().take();
        self.stopping.store(true, Ordering::SeqCst);
        let Some(pid) = pid else { return };
        kill_process_tree(pid);

        // 等待退出监视任务确认进程已被 reap（C# 版同样等待最多 10s：
        // Windows 上进程退出与文件句柄释放之间有延迟，升级 dsh 前若进程仍在，
        // npm 会因文件被占用（EBUSY/EPERM）跳过后继续，留下半新半旧的安装）。
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(10);
        while std::time::Instant::now() < deadline {
            if self.reaped.load(Ordering::SeqCst) {
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(100));
        }
    }
}

impl Default for HostProcess {
    fn default() -> Self {
        Self::new()
    }
}

fn path_separator() -> &'static str {
    if cfg!(windows) {
        ";"
    } else {
        ":"
    }
}

/// 平台相关的进程设置：Unix 上放到独立进程组，便于整组终止。
pub fn apply_platform_process_setup(cmd: &mut tokio::process::Command) {
    #[cfg(unix)]
    {
        use std::os::unix::process::CommandExt;
        cmd.as_std_mut().process_group(0);
    }
    #[cfg(windows)]
    {
        let _ = cmd;
    }
}

/// 终止整棵进程树：Windows 用 taskkill /T /F；Unix 用进程组 SIGKILL。
fn kill_process_tree(pid: u32) {
    #[cfg(windows)]
    {
        let _ = std::process::Command::new("taskkill.exe")
            .args(["/PID", &pid.to_string(), "/T", "/F"])
            .creation_flags(0x0800_0000) // CREATE_NO_WINDOW
            .output();
    }
    #[cfg(unix)]
    {
        // 进程在独立进程组（process_group(0)）里，pgid == pid，整组杀掉
        unsafe {
            libc::kill(-(pid as i32), libc::SIGKILL);
        }
    }
    wait_for_pid_exit(pid, std::time::Duration::from_secs(10));
}

fn wait_for_pid_exit(pid: u32, timeout: std::time::Duration) {
    let deadline = std::time::Instant::now() + timeout;
    while std::time::Instant::now() < deadline {
        if !pid_alive(pid) {
            return;
        }
        std::thread::sleep(std::time::Duration::from_millis(100));
    }
}

fn pid_alive(pid: u32) -> bool {
    #[cfg(windows)]
    {
        std::process::Command::new("tasklist.exe")
            .args(["/FI", &format!("PID eq {pid}"), "/NH"])
            .creation_flags(0x0800_0000)
            .output()
            .map(|out| {
                let text = String::from_utf8_lossy(&out.stdout);
                text.split_whitespace().any(|tok| tok == pid.to_string())
            })
            .unwrap_or(false)
    }
    #[cfg(unix)]
    {
        std::path::Path::new("/proc").join(pid.to_string()).exists()
    }
}

/// 从错误缓冲中识别"插件加载失败导致崩溃"的情况并提取插件名与日志。
fn try_detect_crash(error_buffer: &[String]) -> Option<CrashInfo> {
    use regex::Regex;

    let entry_pattern = Regex::new(r"failed to apply loader entry\s+\S+\s+\(([^)]+)\)").ok()?;

    let mut names: Vec<String> = Vec::new();
    let mut list_matched = false;

    fn add_name(names: &mut Vec<String>, raw: &str) {
        let name = raw.trim().trim_matches(',').to_string();
        if !name.is_empty() && !names.contains(&name) {
            names.push(name);
        }
    }

    for line in error_buffer {
        if let Some(idx) = line.find("plugin(s) failed to load:") {
            list_matched = true;
            let list = &line[idx + "plugin(s) failed to load:".len()..];
            for part in list.split(';') {
                add_name(&mut names, part);
            }
        }
        for cap in entry_pattern.captures_iter(line) {
            add_name(&mut names, cap.get(1).map(|m| m.as_str()).unwrap_or(""));
        }
    }

    let is_load_failure = list_matched
        || error_buffer
            .iter()
            .any(|l| l.contains("plugin tree failed to load") || l.contains("fatal load failure"));

    if !is_load_failure || names.is_empty() {
        return None;
    }

    let log = error_buffer
        .iter()
        .rev()
        .take(60)
        .rev()
        .cloned()
        .collect::<Vec<_>>()
        .join("\n");
    Some(CrashInfo { plugin_names: names, error_log: log })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crash_detect_via_list_line() {
        let buffer = vec![
            "[dsh] booting".to_string(),
            "3 plugin(s) failed to load: @scope/bad-plugin; other-bad".to_string(),
            "fatal load failure".to_string(),
        ];
        let crash = try_detect_crash(&buffer).expect("should detect");
        assert!(crash.plugin_names.contains(&"@scope/bad-plugin".to_string()));
        assert!(crash.plugin_names.contains(&"other-bad".to_string()));
        assert!(crash.error_log.contains("booting"));
    }

    #[test]
    fn crash_detect_via_entry_line() {
        let buffer = vec![
            "Error: failed to apply loader entry 2 (@deepseek-ai/dsh-web-app) whatever".to_string(),
            "plugin tree failed to load".to_string(),
        ];
        let crash = try_detect_crash(&buffer).expect("should detect");
        assert_eq!(crash.plugin_names, vec!["@deepseek-ai/dsh-web-app".to_string()]);
    }

    #[test]
    fn no_crash_on_normal_errors() {
        let buffer = vec!["some random error output".to_string()];
        assert!(try_detect_crash(&buffer).is_none());
    }

    #[test]
    fn url_pattern_extraction() {
        // 与泵输出逻辑一致的提取方式
        let line = "dsh web: http://127.0.0.1:56137/?token=abc123";
        let extracted = line
            .find(URL_MARKER)
            .map(|idx| line[idx + "dsh web: ".len()..].trim().to_string())
            .unwrap();
        assert_eq!(extracted, "http://127.0.0.1:56137/?token=abc123");
    }
}

#[cfg(windows)]
use std::os::windows::process::CommandExt;

//! 定位 dsh 可执行位置（C# 版 DshLocator 移植）。
//!
//! 优先级：自动安装目录 → 用户设置路径 → PATH 中的 dsh 命令 → npm 全局安装位置。
//! 对 `dsh.cmd` / shell shim 会解析其内容得到真实 `bin.js` 路径，从而用 node 直接运行，
//! 保证标准输出可被重定向解析。

use std::path::{Path, PathBuf};

use serde::Serialize;

use crate::paths;

/// 定位到的 dsh 运行方式：要么 `node_path` + `script_path`（node 跑 CLI 脚本），
/// 要么仅 `node_path`（独立 exe）。
#[derive(Debug, Clone, Serialize)]
pub struct DshRuntime {
    pub node_path: String,
    pub script_path: String,
}

impl DshRuntime {
    pub fn display_name(&self) -> String {
        if self.script_path.is_empty() {
            self.node_path.clone()
        } else {
            format!("{} {}", self.node_path, self.script_path)
        }
    }
}

/// 按优先级查找 dsh。`configured_path` 为用户设置中手动指定的路径。
pub fn find(configured_path: &str) -> Option<DshRuntime> {
    // 0. 自动安装目录中已安装的 dsh
    if let Some(rt) = resolve_managed() {
        return Some(rt);
    }

    // 1. 用户在设置中指定的路径
    let configured = configured_path.trim();
    if !configured.is_empty() {
        if let Some(rt) = resolve_candidate(Path::new(configured), None) {
            return Some(rt);
        }
    }

    let node_path = find_node();

    // 2. PATH 中的 dsh
    if let Some(dsh_on_path) = find_on_path("dsh") {
        if let Some(rt) = resolve_candidate(&dsh_on_path, node_path.clone()) {
            return Some(rt);
        }
    }

    // 3. npm 全局安装的常见位置
    if let Some(node) = node_path {
        for candidate in npm_global_candidates() {
            if candidate.is_file() {
                return Some(DshRuntime {
                    node_path: node.to_string_lossy().into_owned(),
                    script_path: candidate.to_string_lossy().into_owned(),
                });
            }
        }
    }
    None
}

/// 定位自动安装目录中已安装的 dsh，用捆绑 node（或系统 node 兜底）运行其 bin.js。
fn resolve_managed() -> Option<DshRuntime> {
    if !paths::is_dsh_installed() {
        return None;
    }
    let node = if paths::bundled_node().is_file() {
        paths::bundled_node()
    } else {
        find_node()?
    };
    Some(DshRuntime {
        node_path: node.to_string_lossy().into_owned(),
        script_path: paths::dsh_bin_script().to_string_lossy().into_owned(),
    })
}

fn npm_global_candidates() -> Vec<PathBuf> {
    let mut list = Vec::new();
    if cfg!(windows) {
        let appdata = std::env::var("APPDATA").unwrap_or_default();
        let program_files = std::env::var("ProgramFiles").unwrap_or_else(|_| r"C:\Program Files".into());
        let local_appdata = std::env::var("LOCALAPPDATA").unwrap_or_default();
        for root in [appdata.clone(), local_appdata.clone()] {
            if !root.is_empty() {
                let root = PathBuf::from(root);
                list.push(root.join("npm").join("@deepseek-ai").join("dsh").join("lib").join("bin.js"));
                list.push(root.join("npm").join("node_modules").join("@deepseek-ai").join("dsh").join("lib").join("bin.js"));
            }
        }
        let pf = PathBuf::from(program_files);
        list.push(pf.join("nodejs").join("node_modules").join("@deepseek-ai").join("dsh").join("lib").join("bin.js"));
        if !local_appdata.is_empty() {
            list.push(
                PathBuf::from(local_appdata)
                    .join("Programs")
                    .join("nodejs")
                    .join("node_modules")
                    .join("@deepseek-ai")
                    .join("dsh")
                    .join("lib")
                    .join("bin.js"),
            );
        }
    } else {
        let home = paths::user_home_dir();
        for root in [
            PathBuf::from("/usr/local/lib/node_modules"),
            PathBuf::from("/usr/lib/node_modules"),
            home.join(".npm-global").join("lib").join("node_modules"),
            PathBuf::from("/opt/homebrew/lib/node_modules"),
            PathBuf::from("/usr/local/share/npm-global/lib/node_modules"),
        ] {
            list.push(
                root.join("@deepseek-ai")
                    .join("dsh")
                    .join("lib")
                    .join("bin.js"),
            );
        }
    }
    list
}

/// 在 PATH 中查找 node（各平台常见安装位置兜底）。
pub fn find_node() -> Option<PathBuf> {
    if let Some(on_path) = find_on_path("node") {
        return Some(on_path);
    }
    if cfg!(windows) {
        let program_files = std::env::var("ProgramFiles").unwrap_or_else(|_| r"C:\Program Files".into());
        let local_appdata = std::env::var("LOCALAPPDATA").unwrap_or_default();
        let mut candidates = vec![PathBuf::from(program_files).join("nodejs").join("node.exe")];
        if !local_appdata.is_empty() {
            candidates.push(
                PathBuf::from(local_appdata)
                    .join("Programs")
                    .join("nodejs")
                    .join("node.exe"),
            );
        }
        candidates.into_iter().find(|c| c.is_file())
    } else {
        [
            "/usr/local/bin/node",
            "/usr/bin/node",
            "/opt/homebrew/bin/node",
            "/snap/bin/node",
        ]
        .iter()
        .map(PathBuf::from)
        .find(|c| c.is_file())
    }
}

/// 在 PATH 中按名称查找可执行文件。Windows 依次尝试 .exe/.cmd/.bat/无后缀。
pub fn find_on_path(name: &str) -> Option<PathBuf> {
    let path_var = std::env::var("PATH").unwrap_or_default();
    let extensions: &[&str] = if cfg!(windows) {
        &[".exe", ".cmd", ".bat", ""]
    } else {
        &[""]
    };
    for dir in path_var.split(if cfg!(windows) { ';' } else { ':' }) {
        let dir = dir.trim();
        if dir.is_empty() {
            continue;
        }
        for ext in extensions {
            let full = Path::new(dir).join(format!("{name}{ext}"));
            if full.is_file() {
                return Some(full);
            }
        }
    }
    None
}

fn resolve_candidate(path: &Path, fallback_node: Option<PathBuf>) -> Option<DshRuntime> {
    let raw = path.to_string_lossy();
    let lower = raw.to_lowercase();

    if lower.ends_with(".js") {
        if !path.is_file() {
            return None;
        }
        let node = fallback_node.or_else(find_node)?;
        return Some(DshRuntime {
            node_path: node.to_string_lossy().into_owned(),
            script_path: raw.into_owned(),
        });
    }

    if lower.ends_with(".cmd") || lower.ends_with(".bat") {
        // Windows only
        if !path.is_file() {
            return None;
        }
        let node = fallback_node.or_else(find_node)?;
        if let Some(js) = parse_cmd_shim(path) {
            if Path::new(&js).is_file() {
                return Some(DshRuntime {
                    node_path: node.to_string_lossy().into_owned(),
                    script_path: js,
                });
            }
        }
        return Some(DshRuntime {
            node_path: node.to_string_lossy().into_owned(),
            script_path: raw.into_owned(),
        });
    }

    if !cfg!(windows) && lower.ends_with(".sh") {
        if !path.is_file() {
            return None;
        }
        let node = fallback_node.or_else(find_node)?;
        if let Some(js) = parse_shell_shim(path) {
            if Path::new(&js).is_file() {
                return Some(DshRuntime {
                    node_path: node.to_string_lossy().into_owned(),
                    script_path: js,
                });
            }
        }
        return Some(DshRuntime {
            node_path: node.to_string_lossy().into_owned(),
            script_path: raw.into_owned(),
        });
    }

    // 无后缀（unix 直接可执行）或 .exe
    if path.is_file() {
        if cfg!(windows) {
            if lower.ends_with(".exe") {
                return Some(DshRuntime {
                    node_path: raw.into_owned(),
                    script_path: String::new(),
                });
            }
            // windows 上未知类型一律不认
            return None;
        }
        // unix：尝试解析 shell shim（无后缀的 npm shim），失败则按独立可执行处理
        let node = fallback_node.or_else(find_node);
        if let Some(js) = parse_shell_shim(path) {
            if Path::new(&js).is_file() {
                if let Some(node) = node {
                    return Some(DshRuntime {
                        node_path: node.to_string_lossy().into_owned(),
                        script_path: js,
                    });
                }
            }
        }
        return Some(DshRuntime {
            node_path: raw.into_owned(),
            script_path: String::new(),
        });
    }
    None
}

/// 解析 npm 生成的 cmd shim：
/// `@"%~dp0\node.exe" "%~dp0\node_modules\@deepseek-ai\dsh\lib\bin.js" %*`
fn parse_cmd_shim(path: &Path) -> Option<String> {
    let content = std::fs::read_to_string(path).ok()?;
    parse_node_invocation(&content, path)
}

/// 解析 npm 生成的 shell shim（unix）：
/// `"$basedir/node" "$basedir/node_modules/@deepseek-ai/dsh/lib/bin.js" "$@"`
fn parse_shell_shim(path: &Path) -> Option<String> {
    let content = std::fs::read_to_string(path).ok()?;
    parse_node_invocation(&content, path)
}

/// 从 shim 文本中找到 `node ... something.js` 的调用对，把 `%~dp0` / `$basedir`
/// 前缀替换为 shim 所在目录，返回绝对化的 js 路径。
fn parse_node_invocation(content: &str, shim_path: &Path) -> Option<String> {
    let shim_dir = shim_path.parent()?;
    for line in content.lines() {
        let tokens = split_tokens(line);
        for i in 0..tokens.len().saturating_sub(1) {
            let first = tokens[i].to_lowercase();
            let base_ok = {
                let base = Path::new(&first)
                    .file_name()
                    .map(|f| f.to_string_lossy().to_lowercase())
                    .unwrap_or_default();
                base == "node" || base == "node.exe"
            };
            if !base_ok {
                continue;
            }
            let js = &tokens[i + 1];
            if js.to_lowercase().ends_with(".js") {
                let expanded = expand_shim_var(js, shim_dir);
                let abs = if Path::new(&expanded).is_absolute() {
                    expanded
                } else {
                    shim_dir.join(expanded).to_string_lossy().into_owned()
                };
                return Some(abs);
            }
        }
    }
    None
}

/// 把 `%~dp0`（cmd）与 `$basedir`（sh）展开为 shim 所在目录。
/// npm 生成的 shim 中变量后总是自带分隔符，因此这里只替换为目录本身。
fn expand_shim_var(token: &str, shim_dir: &Path) -> String {
    let dir = shim_dir.to_string_lossy();
    let dir = dir.trim_end_matches(['/', '\\']);
    token.replace("%~dp0", &dir).replace("$basedir", &dir)
}

/// 极简分词：支持双/单引号，忽略引号内空格。
fn split_tokens(line: &str) -> Vec<String> {
    let mut tokens = Vec::new();
    let mut current = String::new();
    let mut in_quote: Option<char> = None;
    let mut has_token = false;
    for ch in line.chars() {
        match in_quote {
            Some(q) => {
                if ch == q {
                    in_quote = None;
                } else {
                    current.push(ch);
                }
            }
            None => match ch {
                '"' | '\'' => {
                    in_quote = Some(ch);
                    has_token = true;
                }
                c if c.is_whitespace() => {
                    if has_token || !current.is_empty() {
                        tokens.push(std::mem::take(&mut current));
                        has_token = false;
                    }
                }
                c => current.push(c),
            },
        }
    }
    if has_token || !current.is_empty() {
        tokens.push(current);
    }
    tokens
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_cmd_shim_content() {
        let content = r#"@ECHO off
@SETLOCAL
@IF EXIST "%~dp0\node.exe" (
  @SET "_prog=%~dp0\node.exe"
) ELSE (
  @SET "_prog=node"
)
"%_prog%"  "%~dp0\node_modules\@deepseek-ai\dsh\lib\bin.js" %*
"#;
        let tokens = split_tokens(content.lines().last().unwrap());
        assert!(tokens.iter().any(|t| t.contains("bin.js")));
    }

    #[test]
    fn parse_shell_shim_content() {
        let content = r#"#!/bin/sh
basedir=$(dirname "$(echo "$0" | sed -e 's,\\,/,g')")

case `uname` in
    *CYGWIN*|*MINGW*|*MSYS*) ;;
esac

if [ -x "$basedir/node" ]; then
  exec "$basedir/node"  "$basedir/node_modules/@deepseek-ai/dsh/lib/bin.js" "$@"
else
  exec node  "$basedir/node_modules/@deepseek-ai/dsh/lib/bin.js" "$@"
fi
"#;
        // 直接喂带 node 调用的一行
        let js = parse_node_invocation(
            r#"exec "$basedir/node"  "$basedir/node_modules/@deepseek-ai/dsh/lib/bin.js" "$@""#,
            Path::new("/usr/local/bin/dsh"),
        )
        .unwrap();
        assert_eq!(js, "/usr/local/bin/node_modules/@deepseek-ai/dsh/lib/bin.js");
    }

    #[test]
    fn tokenize_quoted() {
        let t = split_tokens(r#""C:\Program Files\node.exe" "a b.js" x"#);
        assert_eq!(t[0], r"C:\Program Files\node.exe");
        assert_eq!(t[1], "a b.js");
        assert_eq!(t[2], "x");
    }
}

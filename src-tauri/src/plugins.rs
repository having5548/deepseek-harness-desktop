//! 插件管理（C# 版 PluginManager + PluginCache 移植）：
//! - 从多个可信来源（DSH Market / npm 官方 / npmmirror）拉取插件并去重合并；
//! - 通过捆绑 dsh 的 `plugin --profile web add/remove/update` 安装 / 卸载 / 刷新插件；
//! - 维护崩溃后自动屏蔽的插件清单（持久化在 AppSettings）；
//! - 整合列表持久化到本地缓存，全部来源不可用时仍能展示上次成功获取的插件列表。

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::time::Duration;

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::locator::{self, DshRuntime};
use crate::paths;

/// DSH Market 数据源（Web 站与插件版共用，每日更新）。
const DSH_MARKET_DATA_URL: &str =
    "https://raw.githubusercontent.com/2BingLing/dsh-market/master/data/plugins.json";

const MAX_PER_SOURCE: usize = 100;
const MAX_MERGED_PLUGINS: usize = 150;

/// profile 模板内置、不应卸载的 bundle。
pub const BUILTIN_BUNDLES: &[&str] = &[
    "@deepseek-ai/dsh-base",
    "@deepseek-ai/dsh-web-app",
    "@deepseek-ai/dsh-headless",
];

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum PluginSourceKind {
    /// DSH Market 风格的 JSON 数据文件。
    MarketJson,
    /// npm registry 搜索接口。
    NpmSearch,
}

/// 一个可选的插件来源。
#[derive(Debug, Clone, Serialize)]
pub struct PluginSource {
    pub id: &'static str,
    pub name: &'static str,
    pub url: String,
    pub kind: PluginSourceKind,
}

/// 内置可信插件来源（官方 / 官方镜像，无来历不明者）。
pub fn all_sources() -> Vec<PluginSource> {
    vec![
        PluginSource {
            id: "dsh-market",
            name: "DSH Market",
            url: DSH_MARKET_DATA_URL.to_string(),
            kind: PluginSourceKind::MarketJson,
        },
        PluginSource {
            id: "npm",
            name: "npm 官方",
            url: format!("https://registry.npmjs.org/-/v1/search?text=keywords:dsh-plugin&size={MAX_PER_SOURCE}"),
            kind: PluginSourceKind::NpmSearch,
        },
        PluginSource {
            id: "npmmirror",
            name: "npmmirror 镜像",
            url: format!("https://registry.npmmirror.com/-/v1/search?text=dsh-plugin&size={MAX_PER_SOURCE}"),
            kind: PluginSourceKind::NpmSearch,
        },
    ]
}

pub fn find_source(id: &str) -> Option<PluginSource> {
    all_sources().into_iter().find(|s| s.id.eq_ignore_ascii_case(id))
}

/// 持久化的单个插件条目（多来源合并后的展示/缓存模型）。
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct PluginEntry {
    #[serde(rename = "PackageName")]
    pub package_name: String,
    #[serde(rename = "RepoFullName")]
    pub repo_full_name: String,
    #[serde(rename = "RepoUrl")]
    pub repo_url: String,
    #[serde(rename = "Author")]
    pub author: String,
    #[serde(rename = "Description")]
    pub description: String,
    #[serde(rename = "Stars")]
    pub stars: i64,
    #[serde(rename = "Score")]
    pub score: i64,
    #[serde(rename = "NeedsConfig")]
    pub needs_config: bool,
    #[serde(rename = "InstallSpec")]
    pub install_spec: String,
    /// 该插件来自哪些来源（来源名称，去重有序）。
    #[serde(rename = "Sources")]
    pub sources: Vec<String>,
}

/// 多来源获取的结果。
#[derive(Debug, Clone, Serialize)]
pub struct PluginFetchResult {
    pub plugins: Vec<PluginEntry>,
    pub failed_sources: Vec<String>,
}

/// dsh plugin 命令执行结果。
#[derive(Debug, Clone, Serialize)]
pub struct PluginCommandResult {
    pub success: bool,
    pub output: String,
}

/// 插件列表持久化文件（单一文件，JSON 格式，字段与 C# 版一致以复用旧缓存）。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct PluginCacheFile {
    pub version: i64,
    pub saved_at: String,
    pub sources: Vec<String>,
    pub plugins: Vec<PluginEntry>,
}

impl Default for PluginCacheFile {
    fn default() -> Self {
        Self { version: 1, saved_at: String::new(), sources: Vec::new(), plugins: Vec::new() }
    }
}

pub fn cache_file() -> PathBuf {
    paths::settings_dir().join("plugins-cache.json")
}

/// 保存（覆盖写）插件缓存。失败静默，不影响主流程。
pub fn save_cache(cache: &PluginCacheFile) {
    let mut cache = cache.clone();
    cache.version = 1;
    let now = chrono_now();
    cache.saved_at = now;
    if let Some(dir) = cache_file().parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    if let Ok(json) = serde_json::to_string_pretty(&cache) {
        let _ = std::fs::write(cache_file(), json);
    }
}

/// 读取插件缓存；不存在或损坏返回 None。
pub fn load_cache() -> Option<PluginCacheFile> {
    let bytes = std::fs::read(cache_file()).ok()?;
    let cache: PluginCacheFile = serde_json::from_slice(&bytes).ok()?;
    (!cache.plugins.is_empty()).then_some(cache)
}

fn chrono_now() -> String {
    // 不引 chrono，用系统时间格式化成 ISO-8601 本地近似值
    let secs = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    // 简易 UTC 格式化（展示用途）
    let days_total = secs / 86400;
    let rem = secs % 86400;
    let (h, m, s) = (rem / 3600, (rem % 3600) / 60, rem % 60);
    // 从 1970-01-01 推算年月日
    let (year, month, day) = civil_from_days(days_total as i64);
    format!("{year:04}-{month:02}-{day:02}T{h:02}:{m:02}:{s:02}Z")
}

fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = (z - era * 146_097) as u64;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
    let y = yoe as i64 + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (if m <= 2 { y + 1 } else { y }, m, d)
}

/// 依次从给定来源拉取插件（每个来源独立容错，单源失败不影响其他源）。
pub async fn fetch(sources: &[PluginSource]) -> PluginFetchResult {
    let mut plugins = Vec::new();
    let mut failed = Vec::new();
    for source in sources {
        let client = reqwest::Client::builder()
            .user_agent("dsh-desktop")
            .timeout(Duration::from_secs(30))
            .build();
        match client {
            Err(_) => failed.push(source.name.to_string()),
            Ok(client) => match fetch_source(&client, source).await {
                Ok(list) => plugins.extend(list),
                Err(_) => failed.push(source.name.to_string()),
            },
        }
    }
    PluginFetchResult { plugins, failed_sources: failed }
}

async fn fetch_source(client: &reqwest::Client, source: &PluginSource) -> Result<Vec<PluginEntry>, String> {
    let json: Value = client
        .get(&source.url)
        .send()
        .await
        .map_err(|e| e.to_string())?
        .error_for_status()
        .map_err(|e| e.to_string())?
        .json()
        .await
        .map_err(|e| e.to_string())?;

    let mut list = match source.kind {
        PluginSourceKind::MarketJson => parse_market(&json, source)?,
        PluginSourceKind::NpmSearch => parse_npm_search(&json, source)?,
    };
    list.sort_by(|a, b| b.score.cmp(&a.score).then(b.stars.cmp(&a.stars)));
    list.truncate(MAX_PER_SOURCE);
    Ok(list)
}

fn parse_market(json: &Value, source: &PluginSource) -> Result<Vec<PluginEntry>, String> {
    let mut plugins = Vec::new();
    let Some(items) = json.get("plugins").and_then(Value::as_array) else {
        return Ok(plugins);
    };
    for item in items {
        let name = str_at(item, &["name"]).unwrap_or_default();
        let full_name = str_at(item, &["fullName"])
            .or_else(|| str_at(item, &["id"]))
            .unwrap_or_default();
        let description = str_at(item, &["descriptionZh"])
            .or_else(|| str_at(item, &["description"]))
            .unwrap_or_default();
        let stars = int_at(item, &["stars"]);
        let score = int_at(item, &["score", "total"]).max(int_at(item, &["total"]));
        let needs_config = bool_at(item, &["install", "needsConfig"]);
        let Some(install_spec) = parse_install_spec(item) else {
            // 无法通过 `dsh plugin add` 安装的（如 skill 型）跳过
            continue;
        };
        if name.is_empty() {
            continue;
        }
        let repo_url = normalize_repo_url(Some(&full_name)).unwrap_or_default();
        let author = extract_author(Some(&repo_url), Some(&full_name)).unwrap_or_default();
        plugins.push(PluginEntry {
            package_name: name.clone(),
            repo_full_name: full_name,
            repo_url,
            author,
            description,
            stars,
            score,
            needs_config,
            install_spec,
            sources: vec![source.name.to_string()],
        });
    }
    Ok(plugins)
}

fn parse_npm_search(json: &Value, source: &PluginSource) -> Result<Vec<PluginEntry>, String> {
    let mut plugins = Vec::new();
    let Some(objects) = json.get("objects").and_then(Value::as_array) else {
        return Ok(plugins);
    };
    for obj in objects {
        let Some(pkg) = obj.get("package") else { continue };
        let Some(name) = str_at(pkg, &["name"]) else { continue };
        if name.is_empty() {
            continue;
        }
        let description = str_at(pkg, &["description"]).unwrap_or_default();
        let repo_url = normalize_repo_url(str_at(pkg, &["links", "repository"]).as_deref()).unwrap_or_default();
        let full_name = repo_url
            .strip_prefix("https://github.com/")
            .unwrap_or_default()
            .to_string();
        let score = obj
            .get("score")
            .and_then(|s| s.get("final"))
            .and_then(Value::as_f64)
            .map(|f| (f * 100.0).round() as i64)
            .unwrap_or(0);
        let author = extract_author(Some(&repo_url), Some(&full_name)).unwrap_or_default();
        plugins.push(PluginEntry {
            package_name: name.clone(),
            repo_full_name: full_name,
            repo_url,
            author,
            description,
            stars: 0,
            score,
            needs_config: false,
            install_spec: name.clone(),
            sources: vec![source.name.to_string()],
        });
    }
    Ok(plugins)
}

/// 从 `install.commands` 提取 `dsh plugin ... add <spec>` 中的安装包 spec（支持 add 后带选项）。
fn parse_install_spec(item: &Value) -> Option<String> {
    let commands = item.get("install")?.get("commands")?.as_array()?;
    let re = regex::Regex::new(r"\badd\s+(?:-\S+\s+)*(\S+)").ok()?;
    for cmd in commands {
        let Some(text) = cmd.as_str() else { continue };
        if let Some(cap) = re.captures(text) {
            let spec = cap
                .get(1)
                .map(|m| m.as_str().trim().trim_matches(['"', '\'']))
                .unwrap_or("");
            if !spec.is_empty() {
                return Some(spec.to_string());
            }
        }
    }
    None
}

/// 按「GitHub 仓库链接 + 作者」去重合并多来源条目，取各来源最优信息，按实用分排序。
pub fn merge_and_dedupe(raw: Vec<PluginEntry>) -> Vec<PluginEntry> {
    let mut merged: BTreeMap<String, PluginEntry> = BTreeMap::new();
    for p in raw {
        let Some(key) = dedup_key(&p) else { continue };
        merged
            .entry(key)
            .and_modify(|existing| merge_into(existing, &p))
            .or_insert(p);
    }
    let mut list: Vec<PluginEntry> = merged.into_values().collect();
    list.sort_by(|a, b| b.score.cmp(&a.score).then(b.stars.cmp(&a.stars)));
    list.truncate(MAX_MERGED_PLUGINS);
    list
}

/// 去重键：GitHub 仓库 URL（小写），无仓库时退回包名。
fn dedup_key(p: &PluginEntry) -> Option<String> {
    let repo = normalize_repo_url(Some(&p.repo_url));
    if let Some(repo) = repo {
        return Some(format!("repo:{}", repo.to_lowercase()));
    }
    let name = p.package_name.trim().to_lowercase();
    (!name.is_empty()).then(|| format!("pkg:{name}"))
}

/// 合并两个来源的同一条插件：信息取优，来源并集。
fn merge_into(a: &mut PluginEntry, b: &PluginEntry) {
    if a.package_name.is_empty() {
        a.package_name = b.package_name.clone();
    }
    if a.repo_full_name.is_empty() {
        a.repo_full_name = b.repo_full_name.clone();
    }
    if a.repo_url.is_empty() {
        a.repo_url = b.repo_url.clone();
    }
    if a.author.is_empty() {
        a.author = b.author.clone();
    }
    if a.description.is_empty() {
        a.description = b.description.clone();
    }
    if a.install_spec.is_empty() {
        a.install_spec = b.install_spec.clone();
    }
    a.stars = a.stars.max(b.stars);
    a.score = a.score.max(b.score);
    a.needs_config = a.needs_config || b.needs_config;
    if !a.sources.contains(&b.sources.first().cloned().unwrap_or_default()) {
        a.sources.extend(b.sources.iter().cloned());
    }
}

/// 把各类仓库链接规范化为 https://github.com/owner/repo 形式；无法识别返回 None。
pub fn normalize_repo_url(raw: Option<&str>) -> Option<String> {
    let raw = raw?.trim();
    if raw.is_empty() {
        return None;
    }
    let mut s = raw.to_string();
    if s.to_lowercase().starts_with("git+") {
        s = s[4..].to_string();
    }
    if s.to_lowercase().starts_with("github:") {
        s = format!("https://github.com/{}", &s[7..]);
    }
    // 裸的 owner/repo
    let parts: Vec<&str> = s.split('/').collect();
    if parts.len() == 2 && !parts[0].is_empty() && !parts[1].is_empty() && !s.contains(' ') {
        return Some(format!("https://github.com/{}", s.trim_end_matches('/')));
    }
    if let Ok(url) = url::Url::parse(&s) {
        if url.host_str().map(|h| h.eq_ignore_ascii_case("github.com")).unwrap_or(false) {
            let mut path = url.path().trim_matches('/').to_string();
            if path.to_lowercase().ends_with(".git") {
                path.truncate(path.len() - 4);
            }
            if !path.is_empty() {
                return Some(format!("https://github.com/{path}"));
            }
        }
    }
    None
}

/// 从仓库 URL / fullName 提取作者（owner）。
pub fn extract_author(repo_url: Option<&str>, repo_full_name: Option<&str>) -> Option<String> {
    let repo = normalize_repo_url(repo_url).or_else(|| normalize_repo_url(repo_full_name))?;
    let path = repo.strip_prefix("https://github.com/")?;
    let idx = path.find('/')?;
    if idx > 0 {
        Some(path[..idx].to_string())
    } else {
        None
    }
}

/// 执行 `dsh plugin --profile web <args...>`，并把捆绑运行时目录注入 PATH 以便找到 pnpm。
pub async fn run_plugin_command(args: &[&str]) -> PluginCommandResult {
    let Some(runtime) = locator::find("") else {
        return PluginCommandResult { success: false, output: "未找到 dsh 运行时。".into() };
    };
    if runtime.script_path.is_empty() {
        return PluginCommandResult {
            success: false,
            output: "插件管理需要 node + bin.js 运行方式。".into(),
        };
    }
    run_with_runtime(&runtime, args).await
}

async fn run_with_runtime(runtime: &DshRuntime, args: &[&str]) -> PluginCommandResult {
    let mut cmd = tokio::process::Command::new(&runtime.node_path);
    cmd.arg(&runtime.script_path)
        .arg("plugin")
        .arg("--profile")
        .arg("web")
        .args(args)
        .current_dir(paths::user_home_dir())
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped())
        .stdin(std::process::Stdio::null());

    // 注入 PATH：`dsh plugin` 内部 spawnSync('pnpm') 依赖 PATH 找到捆绑的 pnpm。
    // unix 下 npm -g --prefix 的 shim 位于 runtime/bin/，也要加入。
    if let Some(dir) = std::path::Path::new(&runtime.node_path).parent() {
        let existing = std::env::var("PATH").unwrap_or_default();
        let sep = if cfg!(windows) { ";" } else { ":" };
        let mut prefix = format!("{}{}", dir.to_string_lossy(), sep);
        if !cfg!(windows) {
            prefix.push_str(&format!("{}{}", dir.join("bin").to_string_lossy(), sep));
        }
        cmd.env("PATH", format!("{prefix}{existing}"));
    }
    crate::host::apply_platform_process_setup(&mut cmd);

    let output = cmd.output().await;
    match output {
        Ok(out) => {
            let text = format!(
                "{}\n{}",
                String::from_utf8_lossy(&out.stdout),
                String::from_utf8_lossy(&out.stderr)
            )
            .trim()
            .to_string();
            PluginCommandResult { success: out.status.success(), output: text }
        }
        Err(e) => PluginCommandResult { success: false, output: e.to_string() },
    }
}

/// 安装插件。返回是否成功及输出。
pub async fn install(package_spec: &str) -> PluginCommandResult {
    run_plugin_command(&["add", package_spec]).await
}

/// 卸载插件。返回是否成功及输出。
pub async fn remove(package_name: &str) -> PluginCommandResult {
    run_plugin_command(&["remove", package_name]).await
}

/// 刷新 web profile 的插件树：等价于 `dsh plugin update`（在 profile 目录执行 pnpm update）。
///
/// 为什么需要：dsh 的插件树由 profile 目录（~/.dsh/profiles/web）下的 pnpm 独立管理
/// （自带 pnpm-lock.yaml），而升级 dsh CLI 只更新 CLI 自己的安装目录，不会重解析 profile。
pub async fn refresh_profile() -> PluginCommandResult {
    run_plugin_command(&["update"]).await
}

/// 读取 web profile 中已安装的插件（dsh.profile.bundles），含模板内置 bundle。
pub fn get_installed_plugins() -> Vec<String> {
    let manifest = paths::user_home_dir()
        .join(".dsh")
        .join("profiles")
        .join("web")
        .join("package.json");
    let Ok(bytes) = std::fs::read(manifest) else {
        return Vec::new();
    };
    let Ok(json) = serde_json::from_slice::<Value>(&bytes) else {
        return Vec::new();
    };
    let Some(bundles) = json
        .get("dsh")
        .and_then(|d| d.get("profile"))
        .and_then(|p| p.get("bundles"))
        .and_then(Value::as_array)
    else {
        return Vec::new();
    };
    let mut list: Vec<String> = Vec::new();
    for item in bundles {
        if let Some(name) = item.as_str() {
            if !name.is_empty() && !list.contains(&name.to_string()) {
                list.push(name.to_string());
            }
        }
    }
    list
}

// ── JSON 取值辅助 ───────────────────────────────────────

fn str_at(value: &Value, path: &[&str]) -> Option<String> {
    let mut current = value;
    for key in path {
        current = current.get(key)?;
    }
    current.as_str().map(String::from)
}

fn int_at(value: &Value, path: &[&str]) -> i64 {
    let mut current = value;
    for key in path {
        current = match current.get(key) {
            Some(v) => v,
            None => return 0,
        };
    }
    current.as_i64().unwrap_or(0)
}

fn bool_at(value: &Value, path: &[&str]) -> bool {
    let mut current = value;
    for key in path {
        current = match current.get(key) {
            Some(v) => v,
            None => return false,
        };
    }
    current.as_bool().unwrap_or(false)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn normalize_urls() {
        assert_eq!(
            normalize_repo_url(Some("git+https://github.com/a/b.git")).as_deref(),
            Some("https://github.com/a/b")
        );
        assert_eq!(
            normalize_repo_url(Some("github:a/b")).as_deref(),
            Some("https://github.com/a/b")
        );
        assert_eq!(
            normalize_repo_url(Some("owner/repo")).as_deref(),
            Some("https://github.com/owner/repo")
        );
        assert_eq!(normalize_repo_url(Some("not a repo")), None);
        assert_eq!(normalize_repo_url(Some("")), None);
    }

    #[test]
    fn extract_author_works() {
        assert_eq!(
            extract_author(Some("https://github.com/aaa/proj"), None).as_deref(),
            Some("aaa")
        );
        assert_eq!(extract_author(None, Some("bbb/proj2")).as_deref(), Some("bbb"));
    }

    #[test]
    fn parse_install_spec_extracts_package() {
        let item = json!({
            "install": { "commands": ["dsh plugin --profile web add @scope/pkg@1.0.0 --config x"] }
        });
        assert_eq!(parse_install_spec(&item).as_deref(), Some("@scope/pkg@1.0.0"));
    }

    #[test]
    fn parse_market_item_skips_skill_type() {
        let source = all_sources()[0].clone();
        let json = json!({
            "plugins": [
                {"name": "good-plugin", "fullName": "a/good", "install": {"commands": ["dsh plugin add good-pkg"]}, "stars": 5, "score": {"total": 42}},
                {"name": "skill-only", "fullName": "a/skill"}
            ]
        });
        let list = parse_market(&json, &source).unwrap();
        assert_eq!(list.len(), 1);
        assert_eq!(list[0].package_name, "good-plugin");
        assert_eq!(list[0].install_spec, "good-pkg");
    }

    #[test]
    fn npm_search_parse() {
        let source = all_sources()[1].clone();
        let json = json!({
            "objects": [
                {"package": {"name": "dsh-plugin-x", "description": "desc", "links": {"repository": "https://github.com/x/dsh-plugin-x"}}, "score": {"final": 0.87}}
            ]
        });
        let list = parse_npm_search(&json, &source).unwrap();
        assert_eq!(list.len(), 1);
        assert_eq!(list[0].score, 87);
        assert_eq!(list[0].author, "x");
    }

    #[test]
    fn merge_dedupes_by_repo() {
        let a = PluginEntry {
            package_name: "pkg".into(),
            repo_url: "https://github.com/o/r".into(),
            score: 10,
            stars: 3,
            sources: vec!["A".into()],
            install_spec: "pkg".into(),
            ..Default::default()
        };
        let b = PluginEntry {
            package_name: "pkg-better-name".into(),
            repo_url: "https://github.com/o/r.git".into(),
            score: 20,
            stars: 5,
            sources: vec!["B".into()],
            description: "d".into(),
            install_spec: "pkg-better-name".into(),
            ..Default::default()
        };
        let merged = merge_and_dedupe(vec![a, b]);
        assert_eq!(merged.len(), 1);
        assert_eq!(merged[0].score, 20);
        assert_eq!(merged[0].sources.len(), 2);
    }

    #[test]
    fn cache_roundtrip() {
        let mut cache = PluginCacheFile::default();
        cache.plugins.push(PluginEntry {
            package_name: "p".into(),
            ..Default::default()
        });
        save_cache(&cache);
        let loaded = load_cache().expect("cache should load");
        assert_eq!(loaded.plugins[0].package_name, "p");
    }
}

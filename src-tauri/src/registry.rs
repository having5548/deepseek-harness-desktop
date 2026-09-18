//! npm 源探测（C# 版 DshUpdater 的源测速部分移植）：
//! 对候选源并行请求 dist-tags 端点，选延迟最低且可用者。

use std::time::{Duration, Instant};

use serde::Serialize;
use serde_json::Value;

const DIST_TAGS_PATH: &str = "-/package/@deepseek-ai/dsh/dist-tags";

/// 候选 npm 源（官方 + 国内镜像，规避网络不可达/被墙）。
/// 注意：华为云 npm 镜像是 repo.huaweicloud.com，写成 registry.huaweicloud.com 无法解析。
pub const REGISTRIES: &[(&str, &str)] = &[
    ("npm 官方", "https://registry.npmjs.org/"),
    ("npmmirror", "https://registry.npmmirror.com/"),
    ("腾讯云镜像", "https://mirrors.cloud.tencent.com/npm/"),
    ("华为云镜像", "https://repo.huaweicloud.com/repository/npm/"),
];

const PER_REQUEST_TIMEOUT: Duration = Duration::from_secs(8);
const OVERALL_BUDGET: Duration = Duration::from_secs(10);

/// 可用 npm 源及其测得的延迟与 dist-tags。
#[derive(Debug, Clone, Serialize)]
pub struct DshRegistry {
    pub name: String,
    pub url: String,
    pub latency_ms: u64,
    pub dist_tags: Value,
}

fn http_client() -> reqwest::Client {
    reqwest::Client::builder()
        .user_agent("dsh-desktop")
        .timeout(PER_REQUEST_TIMEOUT)
        .build()
        .expect("构建 HTTP 客户端失败")
}

async fn fetch_dist_tags(client: &reqwest::Client, url: &str) -> Option<Value> {
    let resp = client
        .get(format!("{url}{DIST_TAGS_PATH}"))
        .send()
        .await
        .ok()?
        .error_for_status()
        .ok()?;
    let tags: Value = resp.json().await.ok()?;
    tags.is_object().then_some(tags)
}

/// 对候选源并行 ping，返回延迟最低且可用的源；全部不可达返回 None。
/// 单请求 8s 超时，整体 10s 预算兜底。
pub async fn select_best_registry() -> Option<DshRegistry> {
    let client = http_client();
    let mut handles = Vec::new();
    for (name, url) in REGISTRIES {
        let name = name.to_string();
        let url = url.to_string();
        let client = client.clone();
        handles.push(tokio::spawn(async move {
            let start = Instant::now();
            fetch_dist_tags(&client, &url)
                .await
                .map(|dist_tags| DshRegistry {
                    name,
                    url,
                    latency_ms: start.elapsed().as_millis() as u64,
                    dist_tags,
                })
        }));
    }

    let mut best: Option<DshRegistry> = None;
    let collect = async {
        for h in handles {
            if let Ok(Some(r)) = h.await {
                if best.as_ref().is_none_or(|b| r.latency_ms < b.latency_ms) {
                    best = Some(r);
                }
            }
        }
    };
    let _ = tokio::time::timeout(OVERALL_BUDGET, collect).await;
    best
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn registries_are_https() {
        for (_, url) in REGISTRIES {
            assert!(url.starts_with("https://"));
            assert!(url.ends_with('/'));
        }
        assert_eq!(REGISTRIES.len(), 4);
    }

    #[test]
    fn dist_tags_path_format() {
        assert!(DIST_TAGS_PATH.starts_with("-/package/"));
    }
}

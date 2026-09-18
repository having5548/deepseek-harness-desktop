//! 简化 semver 比较（core + prerelease），与 C# 版 `DshUpdater.CompareVersions` 行为一致。

/// 比较两个版本号。core 相同时：无预发布号 > 有预发布号；预发布号内数字标识 < 字母标识。
pub fn compare_versions(a: &str, b: &str) -> std::cmp::Ordering {
    use std::cmp::Ordering;
    let (ma, pa) = split_version(a);
    let (mb, pb) = split_version(b);
    for i in 0..3 {
        let cmp = ma[i].cmp(&mb[i]);
        if cmp != Ordering::Equal {
            return cmp;
        }
    }
    match (pa.is_empty(), pb.is_empty()) {
        (true, true) => Ordering::Equal,
        (true, false) => Ordering::Greater,
        (false, true) => Ordering::Less,
        (false, false) => compare_prerelease(&pa, &pb),
    }
}

fn split_version(v: &str) -> ([u64; 3], String) {
    let v = v.trim();
    let (core, pre) = match v.find('-') {
        Some(idx) => (&v[..idx], v[idx + 1..].to_string()),
        None => (v, String::new()),
    };
    let mut arr = [0u64; 3];
    for (i, part) in core.split('.').take(3).enumerate() {
        if let Ok(x) = part.parse::<u64>() {
            arr[i] = x;
        }
    }
    (arr, pre)
}

fn compare_prerelease(a: &str, b: &str) -> std::cmp::Ordering {
    use std::cmp::Ordering;
    let as_: Vec<&str> = a.split('.').collect();
    let bs: Vec<&str> = b.split('.').collect();
    let n = as_.len().max(bs.len());
    for i in 0..n {
        match (as_.get(i), bs.get(i)) {
            (None, _) => return Ordering::Less,
            (_, None) => return Ordering::Greater,
            (Some(av), Some(bv)) => {
                if av == bv {
                    continue;
                }
                let an = av.parse::<u64>();
                let bn = bv.parse::<u64>();
                return match (an, bn) {
                    (Ok(x), Ok(y)) => x.cmp(&y),
                    (Ok(_), Err(_)) => Ordering::Less, // 数字标识 < 字母标识
                    (Err(_), Ok(_)) => Ordering::Greater,
                    (Err(_), Err(_)) => av.cmp(bv),
                };
            }
        }
    }
    Ordering::Equal
}

/// 判断是否为可精确安装的版本号（而非 `latest` / `next` 之类的标签）。
pub fn is_exact_version(spec: &str) -> bool {
    let s = spec.trim();
    match s.chars().next() {
        Some(c) => c.is_ascii_digit() && s.contains('.'),
        None => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cmp::Ordering::*;

    #[test]
    fn core_compare() {
        assert_eq!(compare_versions("1.2.3", "1.2.3"), Equal);
        assert_eq!(compare_versions("1.10.0", "1.9.9"), Greater);
        assert_eq!(compare_versions("2.0.0", "10.0.0"), Less);
    }

    #[test]
    fn prerelease_compare() {
        assert_eq!(compare_versions("1.0.0", "1.0.0-rc.1"), Greater);
        assert_eq!(compare_versions("0.1.5-rc.2", "0.1.5-rc.1"), Greater);
        assert_eq!(compare_versions("0.1.5-rc.1", "0.1.5-rc.2"), Less);
        assert_eq!(compare_versions("0.1.5-rc.2", "0.1.5-rc.2"), Equal);
        // 数字标识 < 字母标识
        assert_eq!(compare_versions("1.0.0-1", "1.0.0-a"), Less);
    }

    #[test]
    fn exact_version() {
        assert!(is_exact_version("0.1.5-rc.2"));
        assert!(is_exact_version("1.2.3"));
        assert!(!is_exact_version("latest"));
        assert!(!is_exact_version("next"));
        assert!(!is_exact_version(""));
    }
}

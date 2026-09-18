// 共享 JS 辅助（Tauri withGlobalTauri 模式）
const { invoke } = window.__TAURI__.core;
const { listen } = window.__TAURI__.event;

/** 安全 invoke：出错时把错误写进控制台并抛出 */
async function call(cmd, args) {
  try {
    return await invoke(cmd, args);
  } catch (e) {
    console.error(`invoke(${cmd}) failed:`, e);
    throw e;
  }
}

/** 日志控制台：绑定 pre 元素，提供追加 / 清空 / 自动滚动 */
function bindLogConsole(preEl, maxLines = 300) {
  const lines = [];
  let autoScroll = true;
  preEl.addEventListener('scroll', () => {
    autoScroll = preEl.scrollTop + preEl.clientHeight >= preEl.scrollHeight - 8;
  });
  return {
    append(line) {
      if (!line) return;
      lines.push(line);
      if (lines.length > maxLines) lines.splice(0, lines.length - maxLines);
      preEl.textContent = lines.join('\n');
      if (autoScroll) preEl.scrollTop = preEl.scrollHeight;
    },
    replaceAll(newLines) {
      lines.length = 0;
      for (const l of newLines || []) this.append(l);
    },
    clear() {
      lines.length = 0;
      preEl.textContent = '';
    },
    get length() { return lines.length; },
  };
}

function el(id) {
  return document.getElementById(id);
}

/** 把后端 Phase 渲染到状态页 */
function renderStatus(statusEls, payload) {
  const { phase, title, detail, code } = payload;
  const busy = phase === 'installing' || phase === 'starting';
  const isErr = phase === 'error' || phase === 'exited' || phase === 'timeout';

  if (statusEls.spinner) statusEls.spinner.style.display = busy ? '' : 'none';
  if (statusEls.title) {
    statusEls.title.textContent = title || '';
    statusEls.title.classList.toggle('error', !!isErr);
  }
  if (statusEls.detail) {
    statusEls.detail.textContent = (detail || '') + (code != null ? `\n退出码 ${code}` : '');
  }
  if (statusEls.actions) statusEls.actions.style.display = busy ? 'none' : '';
}

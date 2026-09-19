/* DeepSeek Harness 桌面端 —— 注入式悬浮工具栏
 *
 * ## 为什么用「注入」而不是原生菜单栏
 * dsh web 的鉴权 cookie 是 `SameSite=Strict`，跨站 iframe 永远带不上，所以主窗口
 * 必须**直接停在 dsh 服务页面上**（不能套 iframe 做外壳）。因此应用自己的顶栏
 * 只能注入到该页面里 —— 这就是本脚本的由来，由 Tauri 的 `initialization_script`
 * 在每个页面加载前执行（见 src/main.rs 的 `build_main_window`）。
 *
 * ## 为什么按钮点一下要用 window.open
 * 远端页面（http://127.0.0.1:<port>）拿不到 Tauri IPC：capability 只授权本地来源，
 * 不包含 remote 条目。于是按钮请求通过一个**哨兵地址**发出：
 *     window.open("https://dsh-desktop.invalid/<action>")
 * Rust 侧在 on_new_window / on_navigation 里识别该主机名，执行对应动作并把窗口请求
 * 拦掉（`NewWindowResponse::Deny`）——所以不会真的弹出窗口，页面状态也不受影响。
 * `.invalid` 是 RFC 2606 保留后缀，永不会被解析到真实站点。
 */
(function () {
  'use strict';

  var ACTION_HOST = 'https://dsh-desktop.invalid/';

  if (window.top !== window.self) return; // 只在顶层文档注入
  if (window.__dshToolbarInstalled) return;
  window.__dshToolbarInstalled = true;

  /** 向 Rust 侧投递一个动作请求（不会真的打开窗口）。 */
  function send(action) {
    try {
      window.open(ACTION_HOST + action, '_blank', 'noopener');
    } catch (e) {
      /* 被拦截也无妨：Rust 侧已收到请求 */
    }
  }

  // 简单描边图标（24×24 网格），避免引入外部字体/图标库
  var ICONS = {
    back: '<path d="M15 18l-6-6 6-6"/>',
    forward: '<path d="M9 6l6 6-6 6"/>',
    reload: '<path d="M20.5 12a8.5 8.5 0 1 1-2.5-6"/><path d="M20.5 4.5v4h-4"/>',
    reconnect: '<path d="M12 3.5v6"/><path d="M6.9 6.9a7.2 7.2 0 1 0 10.2 0"/>',
    external:
      '<path d="M13.5 4.5H19v5.5"/><path d="M19 4.5l-8 8"/><path d="M18 14.5V18a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h3.5"/>',
    plugins:
      '<path d="M9 3.5v4H5.5a1.5 1.5 0 0 0-1.5 1.5V12h3.5a2 2 0 1 1 0 4H4v2.5A1.5 1.5 0 0 0 5.5 20H9v-3.5a2 2 0 1 1 4 0V20h3.5a1.5 1.5 0 0 0 1.5-1.5V16h-3.5a2 2 0 1 1 0-4H18V9a1.5 1.5 0 0 0-1.5-1.5H13v-4a2 2 0 1 1-4 0z"/>',
    settings:
      '<circle cx="12" cy="12" r="3"/><path d="M12 2.8l1.2 2.1 2.4-.6.6 2.4 2.1 1.2-1.3 2 1.3 2-2.1 1.2-.6 2.4-2.4-.6L12 21.2l-1.2-2.1-2.4.6-.6-2.4-2.1-1.2 1.3-2-1.3-2 2.1-1.2.6-2.4 2.4.6z"/>',
    logs: '<path d="M4.5 6.5h15"/><path d="M4.5 12h15"/><path d="M4.5 17.5h9"/>',
    update: '<path d="M12 4v11"/><path d="M7.5 10.5L12 15l4.5-4.5"/><path d="M4.5 19.5h15"/>',
    collapse: '<path d="M7 14.5l5-5 5 5"/>',
  };

  var CSS = [
    '#dsh-toolbar{position:fixed;top:8px;left:50%;transform:translateX(-50%);z-index:2147483647;',
    'display:flex;align-items:center;gap:6px;pointer-events:none;',
    'font:400 13px/1 "Segoe UI","Microsoft YaHei UI","PingFang SC",system-ui,sans-serif;',
    '-webkit-user-select:none;user-select:none}',
    '#dsh-toolbar *{box-sizing:border-box}',
    '.dsh-tb-pill{display:flex;align-items:center;gap:2px;padding:4px;border-radius:999px;',
    'background:rgba(255,255,255,.78);border:1px solid rgba(15,23,42,.10);',
    'box-shadow:0 8px 28px rgba(15,23,42,.18),0 1px 2px rgba(15,23,42,.07);',
    'backdrop-filter:blur(16px) saturate(1.8);-webkit-backdrop-filter:blur(16px) saturate(1.8);',
    'pointer-events:auto;transition:opacity .18s ease,transform .18s ease}',
    '#dsh-toolbar.dsh-tb-collapsed .dsh-tb-pill{opacity:0;transform:translateY(-10px) scale(.95);pointer-events:none}',
    '.dsh-tb-btn{display:flex;align-items:center;justify-content:center;width:30px;height:30px;padding:0;',
    'border:0;border-radius:999px;background:transparent;color:#334155;cursor:pointer;',
    'transition:background .14s ease,color .14s ease,transform .14s ease}',
    '.dsh-tb-btn svg{width:16px;height:16px}',
    '.dsh-tb-btn:hover{background:rgba(59,130,246,.15);color:#1d4ed8;transform:translateY(-1px)}',
    '.dsh-tb-btn:active{transform:translateY(0) scale(.93)}',
    '.dsh-tb-sep{width:1px;height:16px;margin:0 3px;border-radius:1px;background:rgba(15,23,42,.12)}',
    '.dsh-tb-toggle{display:flex;align-items:center;justify-content:center;width:22px;height:22px;padding:0;',
    'border:1px solid rgba(15,23,42,.10);border-radius:999px;background:rgba(255,255,255,.78);color:#64748b;',
    'cursor:pointer;pointer-events:auto;box-shadow:0 2px 8px rgba(15,23,42,.14);',
    'backdrop-filter:blur(10px);-webkit-backdrop-filter:blur(10px)}',
    '.dsh-tb-toggle svg{width:12px;height:12px;transition:transform .18s ease}',
    '#dsh-toolbar.dsh-tb-collapsed .dsh-tb-toggle svg{transform:rotate(180deg)}',
    '@media (prefers-color-scheme:dark){',
    '.dsh-tb-pill{background:rgba(22,27,37,.76);border-color:rgba(255,255,255,.11);',
    'box-shadow:0 8px 28px rgba(0,0,0,.5),0 1px 2px rgba(0,0,0,.4)}',
    '.dsh-tb-btn{color:#cbd5e1}',
    '.dsh-tb-btn:hover{background:rgba(96,165,250,.22);color:#93c5fd}',
    '.dsh-tb-sep{background:rgba(255,255,255,.15)}',
    '.dsh-tb-toggle{background:rgba(22,27,37,.76);border-color:rgba(255,255,255,.11);color:#94a3b8}',
    '}',
  ].join('');

  function make(tag, cls, html) {
    var node = document.createElement(tag);
    if (cls) node.className = cls;
    if (html != null) node.innerHTML = html;
    return node;
  }

  function iconButton(title, icon, onClick) {
    var button = make('button', 'dsh-tb-btn');
    button.type = 'button';
    button.title = title;
    button.setAttribute('aria-label', title);
    button.innerHTML =
      '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" ' +
      'stroke-linecap="round" stroke-linejoin="round">' +
      icon +
      '</svg>';
    button.addEventListener('click', function (event) {
      event.preventDefault();
      event.stopPropagation();
      onClick();
    });
    return button;
  }

  function install() {
    // dsh 页面里可能还没有 body（本脚本在页面脚本之前执行）
    if (!document.body) {
      document.addEventListener('DOMContentLoaded', install, { once: true });
      return;
    }

    var style = make('style');
    style.textContent = CSS;
    document.head ? document.head.appendChild(style) : document.documentElement.appendChild(style);

    var host = make('div');
    host.id = 'dsh-toolbar';

    var pill = make('div', 'dsh-tb-pill');
    pill.appendChild(iconButton('后退 (Alt+←)', ICONS.back, function () { history.back(); }));
    pill.appendChild(iconButton('前进 (Alt+→)', ICONS.forward, function () { history.forward(); }));
    pill.appendChild(iconButton('刷新 (Ctrl+R)', ICONS.reload, function () { location.reload(); }));
    pill.appendChild(make('span', 'dsh-tb-sep'));
    pill.appendChild(iconButton('重新连接服务 (Ctrl+Shift+H)', ICONS.reconnect, function () { send('reconnect'); }));
    pill.appendChild(iconButton('在系统浏览器中打开 (Ctrl+Shift+O)', ICONS.external, function () { send('external'); }));
    pill.appendChild(make('span', 'dsh-tb-sep'));
    pill.appendChild(iconButton('插件管理 (Ctrl+Shift+P)', ICONS.plugins, function () { send('plugins'); }));
    pill.appendChild(iconButton('设置 (Ctrl+,)', ICONS.settings, function () { send('settings'); }));
    pill.appendChild(iconButton('启动日志 (Ctrl+L)', ICONS.logs, function () { send('logs'); }));
    pill.appendChild(make('span', 'dsh-tb-sep'));
    pill.appendChild(iconButton('检查更新', ICONS.update, function () { send('update'); }));

    var toggle = make('button', 'dsh-tb-toggle');
    toggle.type = 'button';
    toggle.title = '收起 / 展开工具栏';
    toggle.setAttribute('aria-label', '收起或展开工具栏');
    toggle.innerHTML =
      '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" ' +
      'stroke-linecap="round" stroke-linejoin="round">' +
      ICONS.collapse +
      '</svg>';
    toggle.addEventListener('click', function (event) {
      event.preventDefault();
      event.stopPropagation();
      host.classList.toggle('dsh-tb-collapsed');
    });

    host.appendChild(pill);
    host.appendChild(toggle);
    document.body.appendChild(host);

    // 原菜单栏提供的快捷键，改由页面侧接管
    document.addEventListener(
      'keydown',
      function (event) {
        var key = event.key;
        var handled = true;
        if (event.altKey && key === 'ArrowLeft') history.back();
        else if (event.altKey && key === 'ArrowRight') history.forward();
        else if (event.ctrlKey && !event.shiftKey && (key === 'r' || key === 'R')) location.reload();
        else if (event.ctrlKey && event.shiftKey && (key === 'H' || key === 'h')) send('reconnect');
        else if (event.ctrlKey && event.shiftKey && (key === 'O' || key === 'o')) send('external');
        else if (event.ctrlKey && event.shiftKey && (key === 'P' || key === 'p')) send('plugins');
        else if (event.ctrlKey && key === ',') send('settings');
        else if (event.ctrlKey && !event.shiftKey && (key === 'l' || key === 'L')) send('logs');
        else handled = false;
        if (handled) event.preventDefault();
      },
      true
    );
  }

  install();
})();

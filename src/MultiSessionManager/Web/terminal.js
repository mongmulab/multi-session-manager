// WebView2 하나 안에 세션마다 xterm 인스턴스를 하나씩 둔다.
// 호스트(C#)와는 JSON 메시지로만 이야기한다.
//   호스트 → 여기: create · show · out · exit · dispose · title
//   여기 → 호스트: ready · in · resize
(() => {
  'use strict';

  const TerminalCtor = window.Terminal.Terminal || window.Terminal;
  const FitCtor = window.FitAddon.FitAddon || window.FitAddon;
  const Unicode11Ctor = window.Unicode11Addon.Unicode11Addon || window.Unicode11Addon;

  const panesEl = document.getElementById('panes');
  const emptyEl = document.getElementById('empty');
  const terms = new Map();
  let activeId = null;

  const post = (msg) => window.chrome.webview.postMessage(msg);

  function create(id) {
    if (terms.has(id)) return terms.get(id);

    const pane = document.createElement('div');
    pane.className = 'pane';
    const host = document.createElement('div');
    host.className = 'term';
    pane.appendChild(host);
    panesEl.appendChild(pane);

    const term = new TerminalCtor({
      // Cascadia 에는 한글 글리프가 없어서 D2Coding·맑은 고딕으로 넘어간다.
      fontFamily: '"Cascadia Mono", "D2Coding", "Malgun Gothic", Consolas, monospace',
      fontSize: 14,
      scrollback: 5000,
      cursorBlink: true,
      allowProposedApi: true,
      theme: { background: '#1e1e1e', foreground: '#d4d4d4' },
    });
    const fit = new FitCtor();
    term.loadAddon(fit);
    term.loadAddon(new Unicode11Ctor());
    // 한글·이모지 폭을 Claude Code(Node)와 같은 유니코드 11 기준으로 센다. 어긋나면 커서가 밀린다.
    term.unicode.activeVersion = '11';
    term.open(host);

    term.onData((data) => post({ t: 'in', id, data }));
    term.onResize(({ cols, rows }) => post({ t: 'resize', id, cols, rows }));

    // 글자를 골라 둔 상태의 Ctrl+C 는 복사로, 아니면 그대로 CLI 에 보내 작업을 멈추게 한다.
    term.attachCustomKeyEventHandler((e) => {
      if (e.type === 'keydown' && e.ctrlKey && !e.shiftKey && e.key === 'c' && term.hasSelection()) {
        navigator.clipboard.writeText(term.getSelection());
        term.clearSelection();
        return false;
      }
      return true;
    });

    const entry = { term, fit, pane };
    terms.set(id, entry);
    return entry;
  }

  function show(id) {
    for (const [key, entry] of terms) entry.pane.classList.toggle('active', key === id);
    activeId = terms.has(id) ? id : null;
    emptyEl.classList.toggle('hidden', activeId !== null);
    if (!activeId) return;

    const entry = terms.get(id);
    // display:none 이던 칸은 크기를 잴 수 없으니 보인 다음 프레임에 맞춘다.
    requestAnimationFrame(() => {
      entry.fit.fit();
      entry.term.focus();
      // 크기가 그대로면 onResize 가 안 불리므로 한 번은 직접 알린다. 호스트는 이걸 보고 프로세스를 띄운다.
      post({ t: 'resize', id, cols: entry.term.cols, rows: entry.term.rows });
    });
  }

  function dispose(id) {
    const entry = terms.get(id);
    if (!entry) return;
    entry.term.dispose();
    entry.pane.remove();
    terms.delete(id);
    if (activeId === id) show(null);
  }

  window.chrome.webview.addEventListener('message', (e) => {
    const m = e.data;
    switch (m.t) {
      case 'create': create(m.id); break;
      case 'show': show(m.id); break;
      case 'out': { const entry = terms.get(m.id); if (entry) entry.term.write(m.data); break; }
      case 'exit': {
        const entry = terms.get(m.id);
        if (entry) entry.term.write(`\r\n\x1b[90m[${m.text}]\x1b[0m\r\n`);
        break;
      }
      case 'dispose': dispose(m.id); break;
    }
  });

  new ResizeObserver(() => {
    const entry = activeId && terms.get(activeId);
    if (entry) entry.fit.fit();
  }).observe(panesEl);

  post({ t: 'ready' });
})();

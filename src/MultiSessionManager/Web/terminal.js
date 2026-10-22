// WebView2 하나 안에 세션마다 xterm 인스턴스를 하나씩 두고, 1·2·4칸으로 나눠 보여 준다.
// 호스트(C#)와는 JSON 메시지로만 이야기한다.
//   호스트 → 여기: create · layout · title · out · exit · dispose
//   여기 → 호스트: ready · in · resize · focus · ratios · command
(() => {
  'use strict';

  const TerminalCtor = window.Terminal.Terminal || window.Terminal;
  const FitCtor = window.FitAddon.FitAddon || window.FitAddon;
  const Unicode11Ctor = window.Unicode11Addon.Unicode11Addon || window.Unicode11Addon;

  const GUTTER = 6;
  const panesEl = document.getElementById('panes');
  const emptyEl = document.getElementById('empty');
  const gutterV = document.createElement('div');
  gutterV.className = 'gutter gutter-v';
  const gutterH = document.createElement('div');
  gutterH.className = 'gutter gutter-h';
  panesEl.append(gutterV, gutterH);

  const terms = new Map();
  const timings = {}; // 세션별 첫 출력이 도착한 시각(페이지 로드 기준 ms)
  const placeholders = [];
  let layout = { mode: 1, slots: [], focus: 0, col: 0.5, row: 0.5 };

  const post = (msg) => window.chrome.webview.postMessage(msg);

  // 칸 번호 → 격자 위치. 열·행 사이의 2번 트랙은 칸막이 자리다.
  const AREA = [
    { col: 1, row: 1 },
    { col: 3, row: 1 },
    { col: 1, row: 3 },
    { col: 3, row: 3 },
  ];

  function create(id) {
    if (terms.has(id)) return terms.get(id);

    const pane = document.createElement('div');
    pane.className = 'pane';
    const head = document.createElement('div');
    head.className = 'pane-head';
    const titleEl = document.createElement('span');
    titleEl.className = 'pane-title';
    const statusEl = document.createElement('span');
    statusEl.className = 'pane-status';
    head.append(titleEl, statusEl);
    const host = document.createElement('div');
    host.className = 'term';
    pane.append(head, host);
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
    term.onResize(({ cols, rows }) => queueResize(id, cols, rows));

    // 글자를 골라 둔 상태의 Ctrl+C 는 복사로, 아니면 그대로 CLI 에 보내 작업을 멈추게 한다.
    term.attachCustomKeyEventHandler((e) => {
      // 터미널에 포커스가 있으면 키가 WPF 창까지 가지 않는다. 앱 단축키는 여기서 가로채 호스트로 보낸다.
      if (e.type === 'keydown' && e.ctrlKey && e.altKey && !e.shiftKey && ['Digit1', 'Digit2', 'Digit4'].includes(e.code)) {
        post({ t: 'command', name: 'layout' + e.code.slice(5) });
        return false;
      }
      if (e.type === 'keydown' && e.ctrlKey && e.shiftKey && !e.altKey && e.code === 'KeyT') {
        post({ t: 'command', name: 'new' });
        return false;
      }
      if (e.type === 'keydown' && e.ctrlKey && !e.shiftKey && e.key === 'c' && term.hasSelection()) {
        navigator.clipboard.writeText(term.getSelection());
        term.clearSelection();
        return false;
      }
      return true;
    });

    // 칸을 누르면 그 칸이 포커스를 받는다. 호스트는 왼쪽 목록 선택을 맞춘다.
    pane.addEventListener('mousedown', () => {
      const slot = layout.slots.indexOf(id);
      if (slot >= 0 && slot !== layout.focus) post({ t: 'focus', slot });
    }, true);

    const entry = { term, fit, pane, titleEl, statusEl, lastSent: '' };
    terms.set(id, entry);
    return entry;
  }

  // 칸막이를 끄는 동안 크기 알림이 수십 번 나간다. ConPTY 가 그때마다 Claude 화면을 다시 그리게 하지 않도록
  // 마지막 크기만 80ms 뒤에 보낸다.
  const resizeTimers = new Map();
  function queueResize(id, cols, rows, force = false) {
    clearTimeout(resizeTimers.get(id));
    resizeTimers.set(id, setTimeout(() => {
      const entry = terms.get(id);
      const key = `${cols}x${rows}`;
      if (!entry || (!force && entry.lastSent === key)) return;
      entry.lastSent = key;
      post({ t: 'resize', id, cols, rows });
    }, 80));
  }

  function placeholder(slot) {
    if (!placeholders[slot]) {
      const el = document.createElement('div');
      el.className = 'slot-empty';
      el.textContent = '왼쪽 목록에서 세션을 고르면 이 칸에 열립니다.';
      el.addEventListener('mousedown', () => post({ t: 'focus', slot }));
      panesEl.appendChild(el);
      placeholders[slot] = el;
    }
    return placeholders[slot];
  }

  function applyLayout() {
    const { mode, slots, focus, col, row } = layout;
    const twoCols = mode >= 2;
    const twoRows = mode === 4;
    panesEl.style.gridTemplateColumns = twoCols ? `${col}fr ${GUTTER}px ${1 - col}fr` : '1fr';
    panesEl.style.gridTemplateRows = twoRows ? `${row}fr ${GUTTER}px ${1 - row}fr` : '1fr';
    gutterV.style.display = twoCols ? 'block' : 'none';
    gutterH.style.display = twoRows ? 'block' : 'none';

    for (const entry of terms.values()) entry.pane.classList.remove('visible', 'focused');
    placeholders.forEach((el) => el && el.classList.remove('visible', 'focused'));

    slots.forEach((id, i) => {
      const area = AREA[i];
      const el = id && terms.has(id) ? terms.get(id).pane : placeholder(i);
      el.style.gridColumn = String(area.col);
      el.style.gridRow = String(area.row);
      el.classList.add('visible');
      el.classList.toggle('focused', mode > 1 && i === focus);
    });

    emptyEl.classList.toggle('hidden', slots.some((id) => id && terms.has(id)) || mode > 1);
    // display 가 바뀐 칸은 다음 프레임에야 크기를 잴 수 있다.
    requestAnimationFrame(() => {
      for (const id of slots) {
        const entry = id && terms.get(id);
        if (!entry) continue;
        entry.fit.fit();
        // 크기가 그대로면 onResize 가 안 불리므로 한 번은 직접 알린다. 호스트는 이걸 보고 프로세스를 띄운다.
        queueResize(id, entry.term.cols, entry.term.rows, true);
      }
      const focused = slots[focus] && terms.get(slots[focus]);
      if (focused) focused.term.focus();
    });
  }

  function fitVisible() {
    for (const id of layout.slots) {
      const entry = id && terms.get(id);
      if (entry) entry.fit.fit();
    }
  }

  // 칸막이 끌기. 비율은 놓을 때 호스트에 알려 저장한다.
  function dragGutter(gutter, axis) {
    gutter.addEventListener('pointerdown', (e) => {
      e.preventDefault();
      gutter.setPointerCapture(e.pointerId);
      document.body.classList.add(axis === 'col' ? 'dragging-col' : 'dragging-row');
      const rect = panesEl.getBoundingClientRect();
      const move = (ev) => {
        const ratio = axis === 'col'
          ? (ev.clientX - rect.left) / rect.width
          : (ev.clientY - rect.top) / rect.height;
        layout[axis] = Math.min(0.85, Math.max(0.15, ratio));
        panesEl.style[axis === 'col' ? 'gridTemplateColumns' : 'gridTemplateRows'] =
          `${layout[axis]}fr ${GUTTER}px ${1 - layout[axis]}fr`;
        fitVisible();
      };
      const up = () => {
        gutter.removeEventListener('pointermove', move);
        gutter.removeEventListener('pointerup', up);
        document.body.classList.remove('dragging-col', 'dragging-row');
        post({ t: 'ratios', col: layout.col, row: layout.row });
      };
      gutter.addEventListener('pointermove', move);
      gutter.addEventListener('pointerup', up);
    });
  }
  dragGutter(gutterV, 'col');
  dragGutter(gutterH, 'row');

  function dispose(id) {
    const entry = terms.get(id);
    if (!entry) return;
    entry.term.dispose();
    entry.pane.remove();
    terms.delete(id);
  }

  window.chrome.webview.addEventListener('message', (e) => {
    const m = e.data;
    switch (m.t) {
      case 'create': create(m.id); break;
      case 'layout':
        layout = { mode: m.mode, slots: m.slots, focus: m.focus, col: m.col, row: m.row };
        applyLayout();
        break;
      case 'title': {
        const entry = terms.get(m.id);
        if (entry) {
          entry.titleEl.textContent = m.title;
          entry.statusEl.textContent = m.status;
          // 색은 목록의 상태 점과 같은 규칙(working · waiting · needs-input · failed · running · idle)
          entry.statusEl.dataset.activity = m.activity || 'idle';
          entry.pane.classList.toggle('attention', !!m.attention);
        }
        break;
      }
      case 'out': {
        const entry = terms.get(m.id);
        if (entry) {
          if (!timings[m.id]) timings[m.id] = Math.round(performance.now());
          entry.term.write(m.data);
        }
        break;
      }
      case 'exit': {
        const entry = terms.get(m.id);
        if (entry) entry.term.write(`\r\n\x1b[90m[${m.text}]\x1b[0m\r\n`);
        break;
      }
      case 'dispose': dispose(m.id); break;
    }
  });

  new ResizeObserver(fitVisible).observe(panesEl);

  // E2E 시험용 읽기 창구. 칸 배치와 각 터미널의 크기·화면 내용을 들여다본다(쓰기 기능은 없다).
  window.__msm = {
    layout: () => ({ ...layout }),
    firstOutputMs: () => ({ ...timings }),
    snapshot: () => layout.slots.map((id) => {
      const entry = id && terms.get(id);
      if (!entry) return { id, empty: true };
      const buf = entry.term.buffer.active;
      const lines = [];
      for (let y = 0; y < buf.length; y++) lines.push(buf.getLine(y)?.translateToString(true) ?? '');
      const rule = lines.map((l) => l.trim()).filter((l) => /^─{10,}$/.test(l)).map((l) => l.length);
      const tail = lines.filter((l) => l.trim()).slice(-12);
      return { id, cols: entry.term.cols, rows: entry.term.rows, ruleWidth: rule.length ? rule[rule.length - 1] : null, tail };
    }),
  };

  post({ t: 'ready', ms: Math.round(performance.now()) });
})();

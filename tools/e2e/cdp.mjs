// WebView2 원격 디버깅 포트에 붙어 실제 입력 경로(xterm textarea → onData → C# → ConPTY)로 키를 넣고 화면을 읽는다.
// 사용: node cdp.mjs <port> <outDir> <step...>
//   key:<Enter|Down|Escape|Backspace>   text:<문자열>   ime:<한글>   wait:<ms>
//   shot:<file.png>            screen:<file.txt>
//   eval:<식>                  결과를 JSON 한 줄로 표준출력에 찍는다
//   drag:<x1>,<y1>,<x2>,<y2>   마우스로 끈다(페이지 좌표)
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';

const [port, outDir, ...steps] = process.argv.slice(2);
const targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
const page = targets.find((t) => t.type === 'page' && t.url.includes('msm.example'));
if (!page) throw new Error('page not found: ' + JSON.stringify(targets.map((t) => t.url)));

const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
let seq = 0;
const pending = new Map();
ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
};
const send = (method, params = {}) => new Promise((r) => { const id = ++seq; pending.set(id, r); ws.send(JSON.stringify({ id, method, params })); });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const INITIALS = 'ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ';
function hangulStages(ch) {
  const c = ch.codePointAt(0) - 0xac00;
  if (c < 0 || c > 11171) return [];
  const l = Math.floor(c / 588), v = Math.floor((c % 588) / 28), t = c % 28;
  const stages = [INITIALS[l], String.fromCodePoint(0xac00 + (l * 21 + v) * 28)];
  if (t) stages.push(ch);
  return stages;
}

const KEYS = {
  Enter: { key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r' },
  Down: { key: 'ArrowDown', code: 'ArrowDown', windowsVirtualKeyCode: 40 },
  Escape: { key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 },
  Backspace: { key: 'Backspace', code: 'Backspace', windowsVirtualKeyCode: 8 },
};

async function evaluate(expression) {
  const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (r.result.exceptionDetails) throw new Error(r.result.exceptionDetails.text + ' :: ' + expression);
  return r.result.result.value;
}

await evaluate("document.querySelector('.pane.focused textarea, .pane.visible textarea')?.focus(); true");

for (const step of steps) {
  const i = step.indexOf(':');
  const kind = step.slice(0, i);
  const arg = step.slice(i + 1);
  if (kind === 'key') {
    const k = KEYS[arg];
    await send('Input.dispatchKeyEvent', { type: 'keyDown', ...k });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', ...k, text: undefined });
  } else if (kind === 'text') {
    await send('Input.insertText', { text: arg });
  } else if (kind === 'ime') {
    // 한글 IME 처럼 한 음절을 ㅎ → 하 → 한 순서로 조합 중 상태를 거쳐 확정한다.
    for (const ch of arg) {
      for (const stage of hangulStages(ch)) {
        await send('Input.imeSetComposition', { text: stage, selectionStart: 1, selectionEnd: 1 });
        await sleep(25);
      }
      await send('Input.insertText', { text: ch });
      await sleep(25);
    }
  } else if (kind === 'wait') {
    await sleep(Number(arg));
  } else if (kind === 'shot') {
    const r = await send('Page.captureScreenshot', { format: 'png' });
    writeFileSync(join(outDir, arg), Buffer.from(r.result.data, 'base64'));
  } else if (kind === 'screen') {
    const text = await evaluate("[...document.querySelectorAll('.pane.focused .xterm-rows > div, .pane.visible .xterm-rows > div')].map(d => d.textContent.replace(/\\s+$/,'')).filter(Boolean).join('\\n')");
    writeFileSync(join(outDir, arg), text ?? '', 'utf8');
  } else if (kind === 'eval') {
    console.log(JSON.stringify(await evaluate(arg)));
  } else if (kind === 'drag') {
    const [x1, y1, x2, y2] = arg.split(',').map(Number);
    await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x1, y: y1 });
    await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: x1, y: y1, button: 'left', clickCount: 1 });
    for (let n = 1; n <= 10; n++) {
      await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: x1 + ((x2 - x1) * n) / 10, y: y1 + ((y2 - y1) * n) / 10, button: 'left', buttons: 1 });
      await sleep(20);
    }
    await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: x2, y: y2, button: 'left', clickCount: 1 });
  }
}
ws.close();

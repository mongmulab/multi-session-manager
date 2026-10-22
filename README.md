# AI CLI 멀티 세션 매니저

Claude Code 를 여러 개 띄우면 PowerShell 창이 금방 대여섯 개가 됩니다.
어느 창이 어느 폴더였는지, 어제 하던 대화가 어느 것이었는지 찾다 보면 작업보다 창 정리에 시간이 더 갑니다.

이 프로그램은 그 창들을 한 화면의 세션 목록으로 모읍니다.
세션마다 이름과 폴더가 붙고, 앱을 껐다 켜도 세션마다 **같은 대화로** 이어집니다.

![세션 3개를 띄운 화면](docs/images/phase1_resumed.png)

> 지금은 0.1.0, 1단계(기반)까지 만든 상태입니다. Windows 10 1809 이상, Windows 전용입니다.

## 할 수 있는 것

- 폴더를 골라 Claude Code 세션을 엽니다. 이름은 폴더 이름으로 시작하고 언제든 바꿀 수 있습니다
- 세션마다 독립된 터미널(ConPTY)이 붙어 입출력이 섞이지 않습니다
- 앱을 다시 켜면 세션을 고르는 순간 이전 대화로 이어서 뜹니다
- 중지 · 다시 시작 · 닫기. 닫아도 Claude 쪽 대화 기록은 지우지 않습니다
- 앱이 어떻게 끝나든(강제 종료 포함) 띄운 claude 프로세스가 남지 않습니다

화면 분할, 입력 대기 알림, Codex·Gemini CLI 는 아직 없습니다. 아래 [단계](#단계)에 순서를 적어 두었습니다.

## 설치와 실행

필요한 것은 세 가지입니다.

| 준비물 | 확인 방법 |
|---|---|
| Claude Code (`claude` 가 PATH 에 있어야 합니다) | `claude --version` |
| .NET 9 SDK | `dotnet --list-sdks` |
| WebView2 런타임 (Windows 11 은 기본 설치) | 없으면 첫 실행 때 상태줄에 안내가 뜹니다 |

```powershell
git clone <이 저장소>
cd 01_multsessionmanager
dotnet run --project src/MultiSessionManager
```

시험은 `dotnet test` 로 돌립니다. 실제 ConPTY 를 띄우는 시험이 들어 있어 Windows 에서만 돕니다.

## 쓰는 법

| 하고 싶은 것 | 방법 |
|---|---|
| 새 세션 | **[+ 새 세션]** 또는 `Ctrl+Shift+T` → 폴더 선택 |
| 이름 바꾸기 | 목록에서 더블클릭, `F2`, 또는 **[이름 변경]** |
| 복사 | 글자를 드래그한 뒤 `Ctrl+C`. 고른 글자가 없으면 `Ctrl+C` 는 그대로 Claude 에 가서 작업을 멈춥니다 |
| 붙여넣기 | `Ctrl+V` |
| 대화 이어 가기 | 멈춘 세션을 고르고 **[다시 시작]** |

세션 목록은 `%APPDATA%\MultiSessionManager\sessions.json` 에 저장됩니다.
녹화나 시연처럼 평소 목록과 떼어 두고 싶을 때는 환경변수 `MSM_STATE_FILE` 로 다른 파일을 지정하면 됩니다.

## 이렇게 동작합니다

```
WPF 창 (세션 목록 · 버튼)
  └ SessionManager ─ 생성 · 이름 변경 · 저장(JSON)
      └ Session ─ ClaudeAdapter 가 만든 명령으로 PseudoConsoleProcess 실행
          └ ConPTY ─ claude.exe          ← 세션마다 하나, 모두 Job Object 안
  └ WebView2 하나 ─ 세션마다 xterm.js 인스턴스 하나
```

몇 가지는 일부러 이렇게 정했습니다.

- **세션 ID 는 앱이 먼저 정합니다.** `claude --session-id <UUID>` 로 띄우고, 다음에는 같은 값으로 `--resume` 합니다. CLI 화면을 읽어 ID 를 알아낼 필요가 없기 때문입니다
- **대화 파일이 있을 때만 재개합니다.** 띄우기만 하고 아무 말도 안 한 세션은 파일이 없어서, 그대로 `--resume` 하면 실패합니다
- **WebView2 는 하나만 씁니다.** 세션마다 따로 두면 브라우저 프로세스가 늘어나기 때문입니다
- **Job Object 로 묶습니다.** 앱이 죽어도 OS 가 자식·손자 프로세스를 함께 끝내기 때문에 백그라운드에 claude 가 쌓이지 않습니다
- **바깥 Claude 세션의 환경변수는 지웁니다.** 이 앱을 Claude Code 안에서 띄우면 `CLAUDECODE` 같은 값이 따라오는데, 그대로 두면 자식 claude 가 자기를 중첩 실행으로 여깁니다

기술을 고른 이유와 버린 대안(Go·Tauri·Electron·Python, Monaco 기반)은 [docs/design/01_기술선정_검토.md](docs/design/01_기술선정_검토.md) 에 있습니다.

## 메모리

이 PC(Windows 11, Claude Code 2.1.307)에서 잰 Private 메모리입니다.
세션 하나는 프롬프트 대기, 나머지는 폴더 신뢰 질문 화면에서 잰 값이라 작업 중일 때와는 다릅니다.

| 세션 수 | 앱 본체 | WebView2 (6개 프로세스 합) | claude 합 |
|---|---|---|---|
| 0 | 41.4MB | 162.1MB | - |
| 1 | 31.5MB | 195.8MB | 500.6MB |
| 3 | 32.9MB | 193.4MB | 1,167.6MB |

세션을 1개에서 3개로 늘려도 앱과 WebView2 쪽은 거의 그대로입니다. 늘어나는 것은 claude 프로세스 자체입니다.
5·10세션 측정과 장시간 출력 시험은 4단계에서 합니다.

## 단계

| 단계 | 내용 | 상태 |
|---|---|---|
| 1 기반 | 세션 생성 · 이름 · 폴더 · 실행 · 재개 · 프로세스 정리 | 완료 |
| 2 레이아웃 | 2 · 4분할, 크기 변경 | 다음 |
| 3 복원 보강 | 분할 배치와 창 위치 저장 | |
| 4 안정화 | 입력 대기 · 완료 표시(Hook), 오류 기록, 반복 생성 · 삭제 시험 | |
| 5 확장 | Codex · Gemini CLI, Git Worktree, diff 보기 | |

## 알려진 점

- 한글은 Claude Code 와 같은 유니코드 11 폭으로 셉니다. 글꼴(Cascadia Mono)에 한글이 없어 맑은 고딕으로 넘어가기 때문에 한글 글자 사이가 조금 벌어져 보입니다
- 한글 입력은 Chromium 의 조합 이벤트로 시험했습니다. Windows 한글 IME 로 직접 친 결과는 사람이 확인하는 항목으로 남아 있습니다
- 실행 중에 탭 이름을 바꾸면 앱 목록에는 바로 보이지만, Claude 입력칸 테두리의 이름은 다음에 시작할 때 바뀝니다

## 라이선스

MIT. 터미널 화면에 쓰는 [xterm.js](https://github.com/xtermjs/xterm.js) 는 MIT 이고, 라이선스 원문을 `src/MultiSessionManager/Web/vendor/LICENSE-xterm.txt` 에 같이 두었습니다.

이 프로그램은 Anthropic 의 공식 도구가 아닙니다.

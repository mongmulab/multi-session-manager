# E2E 도구

실제 앱을 띄우고 실제 claude 를 붙여서 확인합니다. 단위 시험(`dotnet test`)으로는 볼 수 없는 것, 예를 들어 칸 크기를 바꿨을 때 Claude 가 새 폭으로 다시 그리는지를 여기서 봅니다.

| 파일 | 하는 일 |
|---|---|
| `phase2.ps1` | 1→2→4칸, 칸막이 끌기(가로·세로), 창 크기 변경, 칸 클릭 포커스, 1칸 복귀, 재기동 후 배치 복원, 종료 시 프로세스 정리. 19가지를 판정합니다 |
| `phase3.ps1` | 창 위치·크기·최대화 복원, 화면 밖 좌표, 종료 정리, 일반 빌드와 단일 파일 게시본의 기동 시간. 16가지 |
| `phase4.ps1` | Hook 상태 표시(작업 중 → 입력 대기), 새 결과 표시, 권한 확인(거절·허락), 강제 종료 후 잔여 프로세스. 11가지 |
| `plugins.ps1` | 칸마다 Claude 의 상태줄·플러그인(`/` 메뉴)·훅(SessionStart·UserPromptSubmit·Stop·SessionEnd)이 도는지. 8가지 |
| `clone_config.py` | 사용자 Claude 설정을 시험용 폴더로 복제합니다(아래 참고) |
| `Measure-Startup.ps1` | 앱을 여러 번 띄워 기동 구간(App 생성·창·WebView2·페이지·첫 출력)을 잽니다 |
| `AppDriver.psm1` | 앱 띄우기·목록 선택·버튼 누르기(UI Automation)·창 캡처(PrintWindow)·창 크기·자식 프로세스 |
| `cdp.mjs` | WebView2 원격 디버깅으로 키·한글 조합 입력·마우스 끌기를 넣고, 화면과 터미널 버퍼를 읽습니다 |
| `Measure-Memory.ps1` | 앱 / WebView2 / claude 를 나눠 Private 메모리를 잽니다 |

```powershell
dotnet build src/MultiSessionManager
powershell -ExecutionPolicy Bypass -File tools/e2e/phase2.ps1
```

## 판정 방법

xterm 버퍼에서 Claude 가 그린 가로줄(`────`) 길이를 그 칸의 터미널 폭과 대조합니다.
가로줄이 폭보다 4칸 넘게 짧으면, ConPTY 가 새 크기를 Claude 에 전달하지 못했다고 봅니다.

화면 내용은 `window.__msm` 으로 읽습니다. 칸 배치·크기·첫 출력 시각을 읽는 읽기 전용 창구이고, 입력이나 상태를 바꾸는 기능은 없습니다.

## 주의

- 실습 폴더는 `D:\lab\msm-demo` 아래를 씁니다(`-DemoRoot` 로 바꿀 수 있습니다). 처음 여는 폴더는 Claude 의 폴더 신뢰 질문 화면에서 멈춰 있습니다. 판정에는 문제가 없습니다
- 세션 목록은 `%TEMP%\msm-e2e-phase2\state.json` 을 씁니다. 평소 목록은 건드리지 않습니다
- **원격 데스크톱 연결이 끊긴 상태에서는 WPF 부분(왼쪽 목록·상태줄)이 캡처에 검게 나옵니다.** 화면 장치가 없어 WPF 가 그리지 않기 때문입니다. 판정은 UI Automation 과 터미널 버퍼로 하므로 결과에는 영향이 없습니다. 전체 창 캡처가 필요하면 연결한 상태에서 돌립니다
- **연결한 상태에서 돌리는 동안에는 다른 창을 누르지 않습니다.** 시험 앱 창이 앞에 있어야 "보고 있는 세션" 판정이 맞습니다. `phase4.ps1` 은 시작할 때 `Set-MsmActive` 로 창을 앞으로 가져오고, 결과의 "창 활성" 값으로 됐는지 남깁니다
- 스크립트에는 한글이 들어 있어 **UTF-8 BOM 으로 저장해야 합니다.** BOM 이 없으면 Windows PowerShell 5.1 이 cp949 로 읽어, 한글 주석이 다음 줄을 삼킵니다
- 출력을 `Select-Object -First N` 으로 자르면 파이프라인이 거기서 멈춰 뒤의 종료 단계가 돌지 않습니다. 앱이 남습니다

## 시험용 Claude 설정 (모든 E2E)

사용자 설정 그대로 돌리면 렌더러 상태나 폴더 신뢰 화면에 따라 Claude 가 다르게 그려 판정이 흔들리고, 시험이 남긴 기록(기동 중 종료 횟수 등)이 사용자 설정에 쌓입니다.
★시험 세션이 사용자 Hook(대화 기록 올리기 등)을 타지 않게 하려는 것이 가장 큰 이유입니다. `Start-MsmApp` 은 시험용 설정 없이는 앱을 띄우지 않습니다(정말 필요할 때만 `-UseRealConfig`).

그래서 `clone_config.py` 가 `%TEMP%\msm-e2e-config` 에 설정을 복제하고, 앱에만 `CLAUDE_CONFIG_DIR` 로 넘깁니다.

- 플러그인·스킬·명령·CLAUDE.md 는 그대로 옮깁니다
- **훅과 상태줄은 기록기(`record.py`)로 바꿉니다.** 이벤트·매처 구성은 같고, 부르는 것만 다릅니다. 실제 훅이 하는 외부 전송은 일어나지 않습니다
- 첫 실행 안내를 건너뛰고 실습 폴더를 신뢰한 것으로 표시합니다. `--permission-mode=default` 를 주면 권한 확인 화면이 뜹니다(`phase4.ps1`). MCP 서버는 옮기지 않습니다. 모델은 Haiku 입니다
- **로그인 정보는 복사하지 않습니다.** 갱신 토큰을 원래 설정과 같이 쓰면 원래 세션이 로그아웃될 수 있습니다

토큰은 한 번만 따로 받아 둡니다.

```powershell
claude setup-token                                   # 브라우저에서 승인 → 코드를 이 창에 붙여 넣기
Set-Content "$env:USERPROFILE\.msm-e2e-token" "sk-ant-oat01-..." -NoNewline
```

스크립트는 이 파일을 읽어 `CLAUDE_CODE_OAUTH_TOKEN` 으로 앱 프로세스에만 넘기고, 셸의 환경변수는 바로 지웁니다.
기록기는 기록 한 건마다 파일 하나(`hook-records\*.json`)를 씁니다. 네 칸이 한꺼번에 끝나면 훅도 동시에 도는데, 한 파일에 덧붙여 쓰면 서로 덮어써 기록이 빠졌습니다.

- 정해진 시간 대신 "네 칸이 다 그릴 때까지" 기다립니다. 바쁜 PC 에서는 네 칸 첫 화면이 20초 가까이 걸렸습니다
- 시험 프로세스는 DPI 를 아는 상태로 돕니다(`SetProcessDPIAware`). 모르면 125% 배율에서 창 크기가 1.25배로 잡힙니다

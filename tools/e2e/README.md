# E2E 도구

실제 앱을 띄우고 실제 claude 를 붙여서 확인합니다. 단위 시험(`dotnet test`)으로는 볼 수 없는 것, 예를 들어 칸 크기를 바꿨을 때 Claude 가 새 폭으로 다시 그리는지를 여기서 봅니다.

| 파일 | 하는 일 |
|---|---|
| `phase2.ps1` | 1→2→4칸, 칸막이 끌기(가로·세로), 창 크기 변경, 칸 클릭 포커스, 1칸 복귀, 재기동 후 배치 복원, 종료 시 프로세스 정리. 19가지를 판정합니다 |
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
- 스크립트에는 한글이 들어 있어 **UTF-8 BOM 으로 저장해야 합니다.** BOM 이 없으면 Windows PowerShell 5.1 이 cp949 로 읽어, 한글 주석이 다음 줄을 삼킵니다
- 출력을 `Select-Object -First N` 으로 자르면 파이프라인이 거기서 멈춰 뒤의 종료 단계가 돌지 않습니다. 앱이 남습니다

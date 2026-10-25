param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\..\src\MultiSessionManager\bin\Debug\net9.0-windows\MultiSessionManager.exe"),
    [string]$OutDir = (Join-Path $env:TEMP "msm-e2e-phase2"),
    [string]$DemoRoot = "D:\lab\msm-demo",
    [int]$Port = 9334,
    [int]$Settle = 8,
    [string]$ConfigDir = (Join-Path $env:TEMP "msm-e2e-config")
)
# 2단계 E2E: 1→2→4칸, 칸막이 끌기, 창 크기 변경 뒤 Claude 가 새 폭으로 다시 그리는지,
# 다시 켰을 때 배치가 돌아오는지, 닫으면 프로세스가 남지 않는지 본다.
# 판정은 xterm 버퍼에서 Claude 가 그린 가로줄(─) 길이를 터미널 칸 수와 대조해서 한다.
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "AppDriver.psm1") -Force
New-Item -ItemType Directory -Force $OutDir | Out-Null
$cdp = Join-Path $PSScriptRoot "cdp.mjs"
$state = Join-Path $OutDir "state.json"

$names = @(@("API 서버", "api"), @("웹 화면", "web"), @("문서", "docs"), @("배치", "batch"))
$sessions = for ($i = 0; $i -lt $names.Count; $i++) {
    New-Item -ItemType Directory -Force (Join-Path $DemoRoot $names[$i][1]) | Out-Null
    @{ Id = "s$i"; Title = $names[$i][0]; WorkingDirectory = (Join-Path $DemoRoot $names[$i][1]); Agent = "claude";
       AgentSessionId = [guid]::NewGuid().ToString() }
}
# 첫 세션은 1단계 시험에서 대화가 남아 있는 ID 를 그대로 써서 재개 화면도 함께 본다.
$sessions[0].AgentSessionId = "4f0c6a2e-1b7d-4c55-9a51-0d6e3f1a7b01"
@{ Version = 1; Sessions = $sessions; ActiveSessionId = "s0" } | ConvertTo-Json -Depth 5 | Set-Content $state -Encoding utf8
# 사용자 설정을 복제한 시험용 Claude 설정(훅·상태줄은 기록기). 사용자 훅이 시험 세션을 밖으로 보내지 않게 한다.
python (Join-Path $PSScriptRoot "clone_config.py") $ConfigDir @($sessions | ForEach-Object { $_.WorkingDirectory -replace '\\', '/' })
if ($LASTEXITCODE) { throw "시험 설정을 만들지 못했습니다" }
$testEnv = Get-MsmTestEnvironment $ConfigDir

$results = [System.Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    $results.Add([pscustomobject]@{ Check = $Name; Ok = $Ok; Detail = $Detail })
    "{0} {1} — {2}" -f ($(if ($Ok) { "PASS" } else { "FAIL" })), $Name, $Detail
}
function Snap() { (node $cdp $Port $OutDir "eval:JSON.stringify(window.__msm.snapshot())" | ConvertFrom-Json) | ConvertFrom-Json }
function Layout() { (node $cdp $Port $OutDir "eval:JSON.stringify(window.__msm.layout())" | ConvertFrom-Json) | ConvertFrom-Json }
function ClaudeCount($app) { @(Get-MsmChildren $app | Where-Object Name -eq "claude.exe").Count }
# Claude 가 그린 가로줄이 칸 폭을 따라왔는지. 테두리 여백 때문에 몇 칸 짧을 수 있다.
function Fits($pane) { $null -ne $pane.ruleWidth -and $pane.ruleWidth -le $pane.cols -and $pane.ruleWidth -ge ($pane.cols - 4) }
# 정해진 시간 대신 보이는 칸이 모두 그릴 때까지 기다린다(바쁜 PC 에서는 첫 화면이 20초 가까이 걸린다).
function Drawn([int]$Count, [int]$Seconds = 60) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Seconds 1
        try { $s = Snap 2>$null } catch { $s = @() }
    } while (-not ((@($s).Count -eq $Count) -and (@($s | Where-Object { -not (Fits $_) }).Count -eq 0)) -and (Get-Date) -lt $deadline)
    Start-Sleep -Seconds 1
    Snap
}
function Describe($snap) { ($snap | ForEach-Object { if ($_.empty) { "(빈칸)" } else { "$($_.id) $($_.cols)x$($_.rows) 줄$($_.ruleWidth)" } }) -join " | " }

$app = Start-MsmApp -Exe $Exe -State $state -CdpPort $Port -Environment $testEnv
Start-Sleep -Seconds $Settle
Set-MsmWindowSize $app 1600 950
Start-Sleep -Seconds 3

# 1칸
$s = Snap
Check "1칸: 세션 하나만 실행" ((ClaudeCount $app) -eq 1) "claude $(ClaudeCount $app)개 · $(Describe $s)"
Save-MsmShot $app (Join-Path $OutDir "p2_01_one.png")

# 2칸
Invoke-MsmButton $app "2칸"
$s = Drawn 2
Check "2칸: 두 칸이 차고 둘 다 실행" (($s.Count -eq 2) -and ((ClaudeCount $app) -eq 2)) "claude $(ClaudeCount $app)개 · $(Describe $s)"
Check "2칸: Claude 가 칸 폭에 맞춰 그림" ((@($s | Where-Object { -not (Fits $_) }).Count -eq 0)) (Describe $s)
Save-MsmShot $app (Join-Path $OutDir "p2_02_two.png")

# 4칸
Invoke-MsmButton $app "4칸"
$s = Drawn 4
Check "4칸: 네 칸이 차고 넷 다 실행" (($s.Count -eq 4) -and ((ClaudeCount $app) -eq 4)) "claude $(ClaudeCount $app)개 · $(Describe $s)"
Check "4칸: Claude 가 칸 폭에 맞춰 그림" ((@($s | Where-Object { -not (Fits $_) }).Count -eq 0)) (Describe $s)
Save-MsmShot $app (Join-Path $OutDir "p2_03_four.png")

# 세로 칸막이를 30% 자리로 끈다
$before = Snap
# 한가운데는 가로 칸막이와 겹치는 자리라 위쪽 1/4 지점을 잡는다.
$g = (node $cdp $Port $OutDir "eval:JSON.stringify((r => ({x: r.left + r.width / 2, y: r.top + r.height / 4, w: document.getElementById('panes').getBoundingClientRect().width}))(document.querySelector('.gutter-v').getBoundingClientRect()))" | ConvertFrom-Json) | ConvertFrom-Json
$toX = [math]::Round($g.w * 0.3)
node $cdp $Port $OutDir "drag:$([math]::Round($g.x)),$([math]::Round($g.y)),$toX,$([math]::Round($g.y))" | Out-Null
Start-Sleep -Seconds 4
$s = Snap
$l = Layout
Check "칸막이: 비율 0.3 근처로 바뀜" ([math]::Abs($l.col - 0.3) -lt 0.03) ("col={0:N3}" -f $l.col)
Check "칸막이: 왼쪽 칸은 좁아지고 오른쪽은 넓어짐" (($s[0].cols -lt $before[0].cols) -and ($s[1].cols -gt $before[1].cols)) "전 $(Describe $before) → 후 $(Describe $s)"
Check "칸막이: Claude 가 새 폭으로 다시 그림" ((@($s | Where-Object { -not (Fits $_) }).Count -eq 0)) (Describe $s)

# 가로 칸막이를 60% 자리로 끈다(4칸에서만 있다)
$beforeRow = $s
$g = (node $cdp $Port $OutDir "eval:JSON.stringify((r => ({x: r.left + r.width / 4, y: r.top + r.height / 2, top: document.getElementById('panes').getBoundingClientRect().top, h: document.getElementById('panes').getBoundingClientRect().height}))(document.querySelector('.gutter-h').getBoundingClientRect()))" | ConvertFrom-Json) | ConvertFrom-Json
$toY = [math]::Round($g.top + $g.h * 0.6)
node $cdp $Port $OutDir "drag:$([math]::Round($g.x)),$([math]::Round($g.y)),$([math]::Round($g.x)),$toY" | Out-Null
Start-Sleep -Seconds 4
$s = Snap
$l = Layout
Check "가로 칸막이: 비율 0.6 근처로 바뀜" ([math]::Abs($l.row - 0.6) -lt 0.03) ("row={0:N3}" -f $l.row)
Check "가로 칸막이: 위 칸은 길어지고 아래 칸은 짧아짐" (($s[0].rows -gt $beforeRow[0].rows) -and ($s[2].rows -lt $beforeRow[2].rows)) "전 $(Describe $beforeRow) → 후 $(Describe $s)"
Save-MsmShot $app (Join-Path $OutDir "p2_04_dragged.png")

# 창 크기 변경
$before = $s
Set-MsmWindowSize $app 1200 800
Start-Sleep -Seconds 4
$s = Snap
Check "창 축소: 모든 칸이 작아짐" ((@(0..3 | Where-Object { $s[$_].cols -ge $before[$_].cols -or $s[$_].rows -ge $before[$_].rows }).Count -eq 0)) "전 $(Describe $before) → 후 $(Describe $s)"
Check "창 축소: Claude 가 새 폭으로 다시 그림" ((@($s | Where-Object { -not (Fits $_) }).Count -eq 0)) (Describe $s)
Save-MsmShot $app (Join-Path $OutDir "p2_05_small.png")
$small = $s

# 칸을 눌러 포커스를 옮기면 목록 선택도 따라온다
$c = (node $cdp $Port $OutDir "eval:JSON.stringify((r => ({x: r.left + r.width / 2, y: r.top + r.height / 2}))(document.querySelectorAll('.pane.visible')[3].getBoundingClientRect()))" | ConvertFrom-Json) | ConvertFrom-Json
node $cdp $Port $OutDir "drag:$([math]::Round($c.x)),$([math]::Round($c.y)),$([math]::Round($c.x)),$([math]::Round($c.y))" | Out-Null
Start-Sleep -Seconds 2
$l = Layout
Check "칸 클릭: 포커스가 4번째 칸으로" ($l.focus -eq 3) "focus=$($l.focus)"

# 1칸으로 줄이면 포커스 칸의 세션이 남는다
$focusedId = $l.slots[$l.focus]
Invoke-MsmButton $app "1칸"
Start-Sleep -Seconds 3
$l = Layout
$s = Snap
Check "1칸 복귀: 포커스 세션이 남음" (($l.slots.Count -eq 1) -and ($l.slots[0] -eq $focusedId)) "남은 칸 $($l.slots -join ',') (포커스였던 $focusedId)"
Check "1칸 복귀: 넓어진 폭으로 다시 그림" ((Fits $s[0]) -and $s[0].cols -gt $small[3].cols) "4칸일 때 $($small[3].cols)칸 → $(Describe $s)"

# 4칸으로 되돌리고 칸막이 비율을 남긴 채 끈다 → 다시 켜면 배치가 돌아와야 한다
Invoke-MsmButton $app "4칸"
Start-Sleep -Seconds 3
$saved = Layout
$stop = Stop-MsmApp $app
Check "종료: 자식 프로세스가 남지 않음" ($stop.Left -eq 0) "자식 $($stop.Children)개 중 남은 것 $($stop.Left)개"

$app = Start-MsmApp -Exe $Exe -State $state -CdpPort $Port -Environment $testEnv
Start-Sleep -Seconds $Settle
$l = Layout
Check "재기동: 4칸 배치와 칸막이 비율 복원" (($l.mode -eq 4) -and (($l.slots -join ',') -eq ($saved.slots -join ',')) -and ([math]::Abs($l.col - $saved.col) -lt 0.001) -and ([math]::Abs($l.row - $saved.row) -lt 0.001) -and ([math]::Abs($l.col - 0.5) -gt 0.1)) "mode=$($l.mode) slots=$($l.slots -join ',') col=$("{0:N3}" -f $l.col) row=$("{0:N3}" -f $l.row)"
Check "재기동: 보이는 네 칸 모두 실행" ((ClaudeCount $app) -eq 4) "claude $(ClaudeCount $app)개"
Drawn 4 | Out-Null   # 캡처에 Claude 화면이 나오게 그릴 때까지 기다린다
Save-MsmShot $app (Join-Path $OutDir "p2_06_restored.png")
$stop = Stop-MsmApp $app
Check "재기동 후 종료: 자식 프로세스가 남지 않음" ($stop.Left -eq 0) "자식 $($stop.Children)개 중 남은 것 $($stop.Left)개"

$results | Export-Csv (Join-Path $OutDir "results.csv") -NoTypeInformation -Encoding utf8
"--- {0}/{1} PASS · 결과와 캡처: {2}" -f @($results | Where-Object Ok).Count, $results.Count, $OutDir
if (@($results | Where-Object { -not $_.Ok }).Count) { exit 1 }

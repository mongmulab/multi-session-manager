param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\..\src\MultiSessionManager\bin\Debug\net9.0-windows\MultiSessionManager.exe"),
    [string]$OutDir = (Join-Path $env:TEMP "msm-e2e-phase3"),
    [string]$DemoRoot = "D:\lab\msm-demo",
    [int]$Port = 9335,
    [int]$Settle = 8,
    [int]$Runs = 3,
    # 사용자 설정을 복제한 시험용 Claude 설정(clone_config.py). 사용자 설정의 화면 상태에 흔들리지 않는다.
    [string]$ConfigDir = (Join-Path $env:TEMP "msm-e2e-config"),
    # 기동 시간 비교 상대: 단일 파일 게시본. 없으면 시간 비교는 건너뛴다.
    [string]$Published = (Join-Path $PSScriptRoot "..\..\publish\win-x64\MultiSessionManager.exe")
)
# 3단계 E2E: 창 위치·크기·최대화가 다시 켰을 때 돌아오는지, 저장한 위치가 화면 밖이면 보이는 곳에 뜨는지,
# 일반 빌드와 단일 파일 게시본의 기동 시간이 얼마나 다른지 본다.
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "AppDriver.psm1") -Force
Add-Type -AssemblyName System.Windows.Forms
New-Item -ItemType Directory -Force $OutDir | Out-Null
$cdp = Join-Path $PSScriptRoot "cdp.mjs"
$state = Join-Path $OutDir "state.json"
$testEnv = Get-MsmTestEnvironment $ConfigDir

$names = @(@("API 서버", "api"), @("웹 화면", "web"), @("문서", "docs"), @("배치", "batch"))
$sessions = for ($i = 0; $i -lt $names.Count; $i++) {
    New-Item -ItemType Directory -Force (Join-Path $DemoRoot $names[$i][1]) | Out-Null
    @{ Id = "s$i"; Title = $names[$i][0]; WorkingDirectory = (Join-Path $DemoRoot $names[$i][1]); Agent = "claude";
       AgentSessionId = [guid]::NewGuid().ToString() }
}
$sessions[0].AgentSessionId = "4f0c6a2e-1b7d-4c55-9a51-0d6e3f1a7b01"
python (Join-Path $PSScriptRoot "clone_config.py") $ConfigDir @($sessions | ForEach-Object { $_.WorkingDirectory -replace '\\', '/' })
if ($LASTEXITCODE) { throw "시험 설정을 만들지 못했습니다" }
$layout = @{ Mode = 4; Slots = @("s0", "s1", "s2", "s3"); Focus = 0; ColumnRatio = 0.5; RowRatio = 0.5 }
@{ Version = 1; Sessions = $sessions; ActiveSessionId = "s0"; Layout = $layout } | ConvertTo-Json -Depth 5 | Set-Content $state -Encoding utf8

$results = [System.Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    $results.Add([pscustomobject]@{ Check = $Name; Ok = $Ok; Detail = $Detail })
    "{0} {1} — {2}" -f ($(if ($Ok) { "PASS" } else { "FAIL" })), $Name, $Detail
}
function Snap() { (node $cdp $Port $OutDir "eval:JSON.stringify(window.__msm.snapshot())" | ConvertFrom-Json) | ConvertFrom-Json }
function ClaudeCount($app) { @(Get-MsmChildren $app | Where-Object Name -eq "claude.exe").Count }
function Fits($pane) { $null -ne $pane.ruleWidth -and $pane.ruleWidth -le $pane.cols -and $pane.ruleWidth -ge ($pane.cols - 4) }
function Describe($snap) { ($snap | ForEach-Object { if ($_.empty) { "(빈칸)" } else { "$($_.id) $($_.cols)x$($_.rows) 줄$($_.ruleWidth)" } }) -join " | " }
function Rect($r) { "$($r.Left),$($r.Top) $($r.Width)x$($r.Height)$(if ($r.Maximized) { ' 최대화' })" }
function Same($a, $b) { [math]::Abs($a.Left - $b.Left) -le 1 -and [math]::Abs($a.Top - $b.Top) -le 1 -and [math]::Abs($a.Width - $b.Width) -le 1 -and [math]::Abs($a.Height - $b.Height) -le 1 }
function Stopped($app, [string]$when) {
    $stop = Stop-MsmApp $app
    Check "$when 종료: 자식 프로세스가 남지 않음" ($stop.Left -eq 0) "자식 $($stop.Children)개 중 남은 것 $($stop.Left)개$(if ($stop.Left) { " ($($stop.LeftNames))" }) · 정리까지 $($stop.WaitMs)ms"
}
# 정해진 시간 대신 네 칸이 다 그릴 때까지 기다린다. PC 가 바쁘면 첫 화면이 20초 가까이 걸리기도 한다.
function Drawn([int]$Seconds = 60) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Seconds 1
        try { $s = Snap 2>$null } catch { $s = @() }
    } while (-not ((@($s).Count -eq 4) -and (@($s | Where-Object { -not (Fits $_) }).Count -eq 0)) -and (Get-Date) -lt $deadline)
    Start-Sleep -Seconds 2  # 다 그린 직후 크기 조정 한 번이 더 올 수 있다
    Snap
}
function ReadState() { Get-Content $state -Raw -Encoding UTF8 | ConvertFrom-Json }

# 1) 처음 켬: 창을 옮기고 끈다.
# 화면보다 큰 창은 Windows 가 줄이므로, 작은 화면에서도 들어가는 크기로 잡는다.
$want = [pscustomobject]@{ Left = 120; Top = 80; Width = 1400; Height = 820; Maximized = $false }
$app = Start-MsmApp -Exe $Exe -State $state -CdpPort $Port -Environment $testEnv
Start-Sleep -Seconds $Settle
Set-MsmWindowRect $app $want.Left $want.Top $want.Width $want.Height
$s = Drawn
Check "처음 켬: 네 칸이 맞는 폭으로 그림" ((@($s).Count -eq 4) -and (@($s | Where-Object { -not (Fits $_) }).Count -eq 0)) (Describe $s)
$learned = $s
Stopped $app "처음 켬"
$st = ReadState
Check "저장: 창 위치·크기가 파일에 남음" ($null -ne $st.Window -and (Same $st.Window $want)) "Window=$(if ($st.Window) { Rect $st.Window } else { '없음' })"

# 2) 다시 켬: 같은 자리·크기로 뜨고, 네 칸이 그 크기에 맞춰 그린다.
$app = Start-MsmApp -Exe $Exe -State $state -CdpPort $Port -Environment $testEnv
$r = Get-MsmWindowRect $app
Check "재기동: 같은 자리·크기" (Same $r $want) "기대 $(Rect $want) · 실제 $(Rect $r)"
$s = Drawn
Check "재기동: 네 칸이 맞는 폭으로 그림" ((@($s).Count -eq 4) -and (@($s | Where-Object { -not (Fits $_) }).Count -eq 0)) "$(Describe $s) (지난번 $(Describe $learned))"
Check "재기동: 네 칸 모두 실행" ((ClaudeCount $app) -eq 4) "claude $(ClaudeCount $app)개"
Save-MsmShot $app (Join-Path $OutDir "p3_01_restored.png")
Set-MsmMaximized $app
Start-Sleep -Seconds 3
Stopped $app "최대화 후"

# 3) 최대화로 끈 창은 최대화로 뜨고, 되돌리면 원래 크기가 된다.
$app = Start-MsmApp -Exe $Exe -State $state -CdpPort $Port -Environment $testEnv
$r = Get-MsmWindowRect $app
Check "최대화: 최대화로 뜸" ($r.Maximized) (Rect $r)
$s = Drawn
Check "최대화: 넓어진 칸에 맞춰 그림" ((@($s | Where-Object { -not (Fits $_) }).Count -eq 0) -and ($s[0].cols -gt $learned[0].cols)) "$(Describe $s) (보통 창일 때 $(Describe $learned))"
Save-MsmShot $app (Join-Path $OutDir "p3_02_maximized.png")
[MsmWin]::ShowWindow($app.MainWindowHandle, 9) | Out-Null
Start-Sleep -Seconds 2
$r = Get-MsmWindowRect $app
Check "최대화: 되돌리면 원래 크기" ((-not $r.Maximized) -and (Same $r $want)) (Rect $r)
Stopped $app "되돌린 뒤"

# 4) 저장한 위치가 지금 화면 밖이면(모니터를 뺀 경우) 보이는 곳에 뜬다.
$st = ReadState
$st.Window.Left = -30000; $st.Window.Top = -30000; $st.Window.Maximized = $false
$st | ConvertTo-Json -Depth 8 | Set-Content $state -Encoding utf8
$app = Start-MsmApp -Exe $Exe -State $state -Environment $testEnv
$r = Get-MsmWindowRect $app
$vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
$visible = ($r.Left -lt $vs.Right - 100) -and ($r.Left + $r.Width -gt $vs.Left + 100) -and ($r.Top -ge $vs.Top - 10) -and ($r.Top -lt $vs.Bottom - 50)
Check "화면 밖 좌표: 보이는 곳으로 옮겨 뜸" $visible "저장 -30000,-30000 → 실제 $(Rect $r) · 화면 $($vs.Left),$($vs.Top) $($vs.Width)x$($vs.Height)"
Start-Sleep -Seconds 3
Stopped $app "화면 밖 시험"

# 5) 기동 시간: 일반 빌드(apphost + 런타임 DLL 따로)와 단일 파일 게시본을 번갈아 띄워 잰다.
$st = ReadState
$st.Window.Left = $want.Left; $st.Window.Top = $want.Top
$st | ConvertTo-Json -Depth 8 | Set-Content $state -Encoding utf8
if (Test-Path $Published) {
    $timing = foreach ($i in 1..$Runs) {
        & (Join-Path $PSScriptRoot "Measure-Startup.ps1") -Exe $Exe -State $state -Runs 1 -Label "일반 빌드" -Environment $testEnv
        & (Join-Path $PSScriptRoot "Measure-Startup.ps1") -Exe $Published -State $state -Runs 1 -Label "단일 파일" -Environment $testEnv
    }
    $timing | Format-Table -AutoSize | Out-String | Write-Host
    $timing | Export-Csv (Join-Path $OutDir "startup.csv") -NoTypeInformation -Encoding utf8
    function Median($xs) { $a = @($xs | Where-Object { $null -ne $_ } | Sort-Object); if ($a.Count) { $a[[int][math]::Floor(($a.Count - 1) / 2)] } }
    $plain = $timing | Where-Object Label -eq "일반 빌드"
    $single = $timing | Where-Object Label -eq "단일 파일"
    $d1 = Median ($plain | ForEach-Object DotNet); $d2 = Median ($single | ForEach-Object DotNet)
    $a1 = Median ($plain | ForEach-Object AllPanes); $a2 = Median ($single | ForEach-Object AllPanes)
    Check "기동: 단일 파일이 앱 코드에 더 빨리 닿음" ($d2 -lt $d1) "App 생성까지(중앙값) 일반 ${d1}ms · 단일 파일 ${d2}ms"
    Check "기동: 단일 파일이 네 칸 첫 화면도 빠름" ($null -ne $a2 -and $a2 -lt $a1) "네 칸 모두 첫 출력(중앙값) 일반 ${a1}ms · 단일 파일 ${a2}ms"
    Check "기동: 시간 재는 동안 남은 프로세스 없음" (@($timing | Where-Object { $_.Left -gt 0 }).Count -eq 0) "$(@($timing).Count)회"
} else {
    "SKIP 기동 시간 비교 — 게시본이 없습니다: dotnet publish src/MultiSessionManager -p:PublishProfile=win-x64"
}

$results | Export-Csv (Join-Path $OutDir "results.csv") -NoTypeInformation -Encoding utf8
"--- {0}/{1} PASS · 결과와 캡처: {2}" -f @($results | Where-Object Ok).Count, $results.Count, $OutDir
if (@($results | Where-Object { -not $_.Ok }).Count) { exit 1 }

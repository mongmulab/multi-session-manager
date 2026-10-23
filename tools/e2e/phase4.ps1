param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\..\src\MultiSessionManager\bin\Debug\net9.0-windows\MultiSessionManager.exe"),
    [string]$OutDir = (Join-Path $env:TEMP "msm-e2e-phase4"),
    [string]$ConfigDir = (Join-Path $env:TEMP "msm-e2e-config"),
    [string]$DemoRoot = "D:\lab\msm-demo",
    [int]$Port = 9337,
    [int]$Timeout = 90
)
# 4단계 E2E: Claude 의 Hook 으로 받은 상태(작업 중 · 입력 대기 · 확인 필요)가 목록과 칸 머리에 뜨는지,
# 보지 않던 세션이 끝나면 "새 결과" 가 붙고 보면 지워지는지, 앱이 강제로 죽어도 프로세스가 남지 않는지 본다.
# 시험용 설정은 권한을 묻는 모드로 복제한다(사용자 설정이 자동 승인이어도 확인 화면을 띄우려고).
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "AppDriver.psm1") -Force
New-Item -ItemType Directory -Force $OutDir | Out-Null
$cdp = Join-Path $PSScriptRoot "cdp.mjs"
$state = Join-Path $OutDir "state.json"

$names = @(@("API 서버", "api"), @("웹 화면", "web"), @("문서", "docs"), @("배치", "batch"))
$dirs = $names | ForEach-Object { Join-Path $DemoRoot $_[1] }
$dirs | ForEach-Object { New-Item -ItemType Directory -Force $_ | Out-Null }
Remove-Item (Join-Path $dirs[0] "perm-test.txt") -ErrorAction SilentlyContinue
python (Join-Path $PSScriptRoot "clone_config.py") $ConfigDir "--permission-mode=default" @($dirs | ForEach-Object { $_ -replace '\\', '/' })
if ($LASTEXITCODE) { throw "시험 설정을 만들지 못했습니다" }
$testEnv = Get-MsmTestEnvironment $ConfigDir

$sessions = for ($i = 0; $i -lt $names.Count; $i++) {
    @{ Id = "s$i"; Title = $names[$i][0]; WorkingDirectory = $dirs[$i]; Agent = "claude"; AgentSessionId = [guid]::NewGuid().ToString() }
}
$layout = @{ Mode = 4; Slots = @("s0", "s1", "s2", "s3"); Focus = 0; ColumnRatio = 0.5; RowRatio = 0.5 }
@{ Version = 1; Sessions = $sessions; ActiveSessionId = "s0"; Layout = $layout } | ConvertTo-Json -Depth 5 | Set-Content $state -Encoding utf8

$results = [System.Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    $results.Add([pscustomobject]@{ Check = $Name; Ok = $Ok; Detail = $Detail })
    "{0} {1} — {2}" -f ($(if ($Ok) { "PASS" } else { "FAIL" })), $Name, $Detail
}
function Eval([string]$js) { (node $cdp $Port $OutDir "eval:$js" | ConvertFrom-Json) }
function Snap() { (Eval "JSON.stringify(window.__msm.snapshot())") | ConvertFrom-Json }
function Fits($pane) { $null -ne $pane.ruleWidth -and $pane.ruleWidth -le $pane.cols -and $pane.ruleWidth -ge ($pane.cols - 4) }
function Drawn([int]$Seconds = 60) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Seconds 1
        try { $s = Snap 2>$null } catch { $s = @() }
    } while (-not ((@($s).Count -eq 4) -and (@($s | Where-Object { -not (Fits $_) }).Count -eq 0)) -and (Get-Date) -lt $deadline)
}
# 목록 i 번째 항목의 상태 글과 "새 결과" 표시
function Item([int]$i) { @(Get-MsmListText $app)[$i] }
function PaneStatus([int]$i) { Eval "document.querySelectorAll('.pane .pane-status')[$i].textContent" }
function ClickPane([int]$i) {
    $c = (Eval "JSON.stringify((r => ({x: r.left + r.width / 2, y: r.top + r.height / 2}))(document.querySelectorAll('.pane.visible')[$i].getBoundingClientRect()))") | ConvertFrom-Json
    node $cdp $Port $OutDir "drag:$([math]::Round($c.x)),$([math]::Round($c.y)),$([math]::Round($c.x)),$([math]::Round($c.y))" | Out-Null
}
# 상태가 바뀌는 순서를 0.3초마다 적어 가며 기다린다.
function Watch([int]$i, [scriptblock]$Done, [int]$Seconds = $Timeout) {
    $seen = [System.Collections.Generic.List[string]]::new()
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $t = Item $i
        $st = ($t -split ' \| ')[2]
        if ($st -and ($seen.Count -eq 0 -or $seen[$seen.Count - 1] -ne $st)) { $seen.Add($st) }
        if (& $Done $t) { break }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    [pscustomobject]@{ Ok = [bool](& $Done (Item $i)); Seen = ($seen -join " → "); Last = (Item $i) }
}
$records = Join-Path $ConfigDir "hook-records"
function UserHooks([string]$Event, [string]$Session) {
    if (-not (Test-Path $records)) { return 0 }
    @(Get-ChildItem $records -Filter *.json | ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } | Where-Object { $_.event -eq $Event -and $_.session -eq $Session }).Count
}

$app = Start-MsmApp -Exe $Exe -State $state -CdpPort $Port -Environment $testEnv
Drawn
$texts = @(Get-MsmListText $app)
Check "처음: 네 세션 모두 실행 중" (@($texts | Where-Object { $_ -match '실행 중' }).Count -eq 4) ($texts -join " // ")

# 1) 한 턴: 작업 중 → 입력 대기. 사용자 설정의 Hook 도 그대로 돈다(우리 Hook 은 더해질 뿐).
# "보고 있던 세션" 판정은 앱 창이 활성이어야 성립한다. 시험 중에 다른 창을 누르면 이 판정은 실패한다.
$active = Set-MsmActive $app
node $cdp $Port $OutDir "ime:한 단어로만 답해 안녕" "key:Enter" | Out-Null
$w = Watch 0 { param($t) $t -match '입력 대기' }
Check "한 턴: 작업 중을 거쳐 입력 대기" ($w.Ok -and $w.Seen -match '작업 중') "목록 상태 변화: $($w.Seen)"
Check "한 턴: 칸 머리에도 같은 상태" ((PaneStatus 0) -eq "입력 대기") "칸 머리 '$(PaneStatus 0)'"
Check "한 턴: 보고 있던 세션이라 새 결과 표시 없음" ($w.Last -notmatch '새 결과') "$($w.Last) · 창 활성 $active"
$sid0 = $sessions[0].AgentSessionId
Check "사용자 Hook 도 함께 돎" ((UserHooks "UserPromptSubmit" $sid0) -ge 1 -and (UserHooks "Stop" $sid0) -ge 1) "기록기 UserPromptSubmit $(UserHooks 'UserPromptSubmit' $sid0) · Stop $(UserHooks 'Stop' $sid0)"
Save-MsmShot $app (Join-Path $OutDir "p4_01_waiting.png")

# 2) 다른 칸을 보는 사이에 끝나면 "새 결과". 그 칸을 다시 보면 지워진다.
node $cdp $Port $OutDir "ime:다시 한 단어로만 답해 고마워" "key:Enter" | Out-Null
Start-Sleep -Milliseconds 500
ClickPane 3
$w = Watch 0 { param($t) $t -match '입력 대기' -and $t -match '새 결과' }
Check "안 보는 사이 끝남: 새 결과 표시" $w.Ok "$($w.Seen) · $($w.Last)"
Save-MsmShot $app (Join-Path $OutDir "p4_02_fresh.png")
ClickPane 0
Start-Sleep -Seconds 1
Check "다시 보면: 새 결과 지워짐" ((Item 0) -notmatch '새 결과') (Item 0)

# 긴 글을 넣고 곧바로 Enter 를 보내면 Claude 가 붙여넣기의 줄바꿈으로 받는다. 잠깐 쉬고 보낸다.
# 3) 권한 확인: 파일을 만들라고 하면 확인 화면에서 멈춘다 → 확인 필요. 거절하면 확인 필요에서 벗어난다.
node $cdp $Port $OutDir "text:Create a file named perm-test.txt containing hi. Use the Write tool." "wait:700" "key:Enter" | Out-Null
$w = Watch 0 { param($t) $t -match '확인 필요' }
Check "권한 확인: 확인 필요" $w.Ok "목록 상태 변화: $($w.Seen)"
Save-MsmShot $app (Join-Path $OutDir "p4_03_permission.png")
node $cdp $Port $OutDir "key:Escape" | Out-Null
$w = Watch 0 { param($t) $t -notmatch '확인 필요' } 30
Check "권한 거절: 확인 필요에서 벗어남" $w.Ok "$($w.Seen) · 파일 $(if (Test-Path (Join-Path $dirs[0] 'perm-test.txt')) { '생김' } else { '안 생김' })"

# 같은 요청을 이번에는 허락한다(확인 화면의 첫 항목 Yes 에서 Enter) → 일을 마치고 입력 대기.
node $cdp $Port $OutDir "text:Now really create perm-test.txt containing hi with the Write tool." "wait:700" "key:Enter" | Out-Null
$w = Watch 0 { param($t) $t -match '확인 필요' }
node $cdp $Port $OutDir "wait:800" "key:Enter" | Out-Null
$w2 = Watch 0 { param($t) $t -match '입력 대기' }
$made = Test-Path (Join-Path $dirs[0] "perm-test.txt")
Check "권한 허락: 확인 필요 → 입력 대기, 파일 생김" ($w.Ok -and $w2.Ok -and $made) "$($w.Seen) → $($w2.Seen) · 파일 $(if ($made) { '생김' } else { '안 생김' })"

# 4) 앱이 강제로 죽어도(작업 관리자에서 끝내기와 같다) claude 와 conhost 가 남지 않는다.
$kids = @(Get-MsmDescendants $app.Id)
$app.Kill()
$app.WaitForExit(10000) | Out-Null
$deadline = (Get-Date).AddSeconds(10)
do {
    Start-Sleep -Milliseconds 500
    $left = @($kids | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
} while ($left.Count -and (Get-Date) -lt $deadline)
$names = ($kids | Group-Object Name | ForEach-Object { "$($_.Name)×$($_.Count)" }) -join ", "
Check "강제 종료: 자손 프로세스가 남지 않음" ($left.Count -eq 0) "자손 $($kids.Count)개($names) 중 남은 것 $($left.Count)개$(if ($left.Count) { ' ' + (($left | ForEach-Object Name) -join ',') })"

$results | Export-Csv (Join-Path $OutDir "results.csv") -NoTypeInformation -Encoding utf8
"--- {0}/{1} PASS · 결과와 캡처: {2}" -f @($results | Where-Object Ok).Count, $results.Count, $OutDir
if (@($results | Where-Object { -not $_.Ok }).Count) { exit 1 }

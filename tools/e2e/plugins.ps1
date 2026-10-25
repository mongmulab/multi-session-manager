param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\..\src\MultiSessionManager\bin\Debug\net9.0-windows\MultiSessionManager.exe"),
    [string]$OutDir = (Join-Path $env:TEMP "msm-e2e-plugins"),
    [string]$ConfigDir = (Join-Path $env:TEMP "msm-e2e-config"),
    [string]$DemoRoot = "D:\lab\msm-demo",
    # claude setup-token 으로 따로 받은 시험용 토큰. 사용자의 로그인 정보는 쓰지 않는다.
    [string]$TokenFile = (Join-Path $env:USERPROFILE ".msm-e2e-token"),
    [int]$Port = 9336,
    [int]$Timeout = 90
)
# 사용자 설정을 복제한 시험용 설정으로 앱을 띄워, 칸마다 Claude 의 플러그인·훅·상태줄이 도는지 본다.
# 훅과 상태줄은 기록기로 바꿔 두었으므로(clone_config.py) 사용자의 실제 훅이 보내는 곳으로는 아무것도 가지 않는다.
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "AppDriver.psm1") -Force
New-Item -ItemType Directory -Force $OutDir | Out-Null
$cdp = Join-Path $PSScriptRoot "cdp.mjs"
$state = Join-Path $OutDir "state.json"

$names = @(@("API 서버", "api"), @("웹 화면", "web"), @("문서", "docs"), @("배치", "batch"))
$dirs = $names | ForEach-Object { Join-Path $DemoRoot $_[1] }
$dirs | ForEach-Object { New-Item -ItemType Directory -Force $_ | Out-Null }
python (Join-Path $PSScriptRoot "clone_config.py") $ConfigDir @($dirs | ForEach-Object { $_ -replace '\\', '/' })
if ($LASTEXITCODE) { throw "시험 설정을 만들지 못했습니다" }
$hookDir = Join-Path $ConfigDir "hook-records"

$sessions = for ($i = 0; $i -lt $names.Count; $i++) {
    @{ Id = "s$i"; Title = $names[$i][0]; WorkingDirectory = $dirs[$i]; Agent = "claude"; AgentSessionId = [guid]::NewGuid().ToString() }
}
$layout = @{ Mode = 4; Slots = @("s0", "s1", "s2", "s3"); Focus = 0; ColumnRatio = 0.5; RowRatio = 0.5 }
@{ Version = 1; Sessions = $sessions; ActiveSessionId = "s0"; Layout = $layout } | ConvertTo-Json -Depth 5 | Set-Content $state -Encoding utf8
$ids = @($sessions | ForEach-Object { $_.AgentSessionId })

$results = [System.Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    $results.Add([pscustomobject]@{ Check = $Name; Ok = $Ok; Detail = $Detail })
    "{0} {1} — {2}" -f ($(if ($Ok) { "PASS" } else { "FAIL" })), $Name, $Detail
}
function Snap() { (node $cdp $Port $OutDir "eval:JSON.stringify(window.__msm.snapshot())" | ConvertFrom-Json) | ConvertFrom-Json }
function Screen() { node $cdp $Port $OutDir "screen:screen.txt" | Out-Null; Get-Content (Join-Path $OutDir "screen.txt") -Raw -Encoding UTF8 }
function Hooks() { if (Test-Path $hookDir) { @(Get-ChildItem $hookDir -Filter *.json | Sort-Object Name | ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json }) } else { @() } }
function Events([string]$Event) { @(Hooks | Where-Object { $_.event -eq $Event -and $ids -contains $_.session }) }
function WaitFor([scriptblock]$Done, [int]$Seconds = $Timeout) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (& $Done) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 700 }
    [bool](& $Done)
}
function Marked($pane) { @($pane.tail | Where-Object { $_ -match 'MSM-STATUSLINE' }).Count -gt 0 }

$app = Start-MsmApp -Exe $Exe -State $state -CdpPort $Port -Environment (Get-MsmTestEnvironment $ConfigDir $TokenFile)

# 1) 상태줄: 네 칸 모두 기록기가 찍은 표식이 보이고, 칸마다 자기 폴더 이름이 나온다.
# 디버그 포트가 열리기 전의 연결 실패는 흘려보낸다.
$null = WaitFor { try { @(Snap 2>$null | Where-Object { Marked $_ }).Count -eq 4 } catch { $false } }
$s = Snap
$own = for ($i = 0; $i -lt 4; $i++) { $leaf = Split-Path $dirs[$i] -Leaf; [bool](@($s[$i].tail | Where-Object { $_ -match "MSM-STATUSLINE $leaf\b" }).Count) }
Check "상태줄: 네 칸 모두 표시" (@($s | Where-Object { Marked $_ }).Count -eq 4) (($s | ForEach-Object { "$($_.id) $(if (Marked $_) { '표시' } else { '없음' })" }) -join " | ")
Check "상태줄: 칸마다 자기 폴더" (@($own | Where-Object { $_ }).Count -eq 4) (($s | ForEach-Object { "$($_.tail | Where-Object { $_ -match 'MSM-STATUSLINE' } | Select-Object -Last 1)".Trim() }) -join " | ")
Save-MsmShot $app (Join-Path $OutDir "pl_01_statusline.png")

# 2) SessionStart 훅: 네 세션이 각자의 폴더로 한 번씩.
$starts = Events "SessionStart"
$pairs = @($starts | ForEach-Object { "$($_.session)|$($_.cwd)" } | Sort-Object -Unique)
$expect = @(for ($i = 0; $i -lt 4; $i++) { "$($ids[$i])|$($dirs[$i])" })
Check "SessionStart 훅: 네 세션 모두, 자기 폴더로" (@($expect | Where-Object { $pairs -notcontains $_ }).Count -eq 0) "기록 $($starts.Count)건 · 세션 $(@($starts.session | Sort-Object -Unique).Count)개"

# 3) 플러그인: 포커스 칸에서 / 메뉴에 humanizer 플러그인 명령이 뜬다.
node $cdp $Port $OutDir "text:/humaniz" "wait:1500" | Out-Null
$menu = Screen
Check "플러그인: / 메뉴에 humanizer" ($menu -match 'humanizer') (($menu -split "`n" | Where-Object { $_ -match 'humaniz' } | Select-Object -First 3) -join " / ")
node $cdp $Port $OutDir "key:Escape" "wait:300" @(1..10 | ForEach-Object { "key:Backspace" }) "wait:500" | Out-Null

# 4) 대화 한 번: UserPromptSubmit·Stop 훅이 그 세션으로 찍히고 답이 화면에 나온다.
node $cdp $Port $OutDir "ime:한 단어로만 답해 안녕" "key:Enter" | Out-Null
$ok = WaitFor { @(Events "Stop" | Where-Object session -eq $ids[0]).Count -gt 0 }
$submit = @(Events "UserPromptSubmit" | Where-Object session -eq $ids[0])
Check "대화: UserPromptSubmit·Stop 훅" ($ok -and $submit.Count -gt 0) "UserPromptSubmit $($submit.Count) · Stop $(@(Events 'Stop' | Where-Object session -eq $ids[0]).Count) (첫 칸 세션만)"
Save-MsmShot $app (Join-Path $OutDir "pl_02_reply.png")
$other = @(Events "UserPromptSubmit" | Where-Object { $_.session -ne $ids[0] })
Check "대화: 다른 칸에는 입력이 가지 않음" ($other.Count -eq 0) "다른 세션 UserPromptSubmit $($other.Count)건"

# 5) 앱을 닫으면 네 세션 모두 SessionEnd 훅이 돈다(세션 기록을 올리는 훅이 여기 걸린다).
$stop = Stop-MsmApp $app
$null = WaitFor { (@(Events "SessionEnd").session | Sort-Object -Unique).Count -ge 4 } 10
$ends = Events "SessionEnd"
Check "종료: 네 세션 모두 SessionEnd 훅" (@($ids | Where-Object { @($ends.session) -notcontains $_ }).Count -eq 0) "SessionEnd $(@($ends.session | Sort-Object -Unique).Count)/4 · 사유 $(@($ends.reason | Sort-Object -Unique) -join ',')"
Check "종료: 자식 프로세스가 남지 않음" ($stop.Left -eq 0) "자식 $($stop.Children)개 중 남은 것 $($stop.Left)개 · 정리까지 $($stop.WaitMs)ms"

Hooks | ForEach-Object { $_ | ConvertTo-Json -Compress } | Set-Content (Join-Path $OutDir "hooks.jsonl") -Encoding utf8
$results | Export-Csv (Join-Path $OutDir "results.csv") -NoTypeInformation -Encoding utf8
"--- {0}/{1} PASS · 결과와 캡처: {2}" -f @($results | Where-Object Ok).Count, $results.Count, $OutDir
if (@($results | Where-Object { -not $_.Ok }).Count) { exit 1 }

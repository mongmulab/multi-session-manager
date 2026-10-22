param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$State,
    [int]$Runs = 3,
    [int]$Panes = 4,
    [int]$Timeout = 40,
    [string]$Label = "",
    [hashtable]$Environment = @{}
)
# 앱을 여러 번 띄워 기동 구간을 잰다. 값은 모두 "OS 가 프로세스를 만든 뒤 몇 ms" 로 맞춘다.
# 앱 기록(app-YYYYMMDD.log)에서 이번 실행분만 읽는다.
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "AppDriver.psm1") -Force
$log = Join-Path $env:LOCALAPPDATA ("MultiSessionManager\logs\app-{0:yyyyMMdd}.log" -f (Get-Date))

function Plus([string]$line) { if ($line -match '\+\s*(\d+)ms') { [int]$Matches[1] } }
function Num([string]$line) { if ($line -match '프로세스 시작 후 ([\d,]+)ms') { [int]($Matches[1] -replace ',', '') } }
function Lines { if (Test-Path $log) { @(Get-Content $log -Encoding UTF8) } else { @() } }

foreach ($run in 1..$Runs) {
    $skip = (Lines).Count
    $app = Start-MsmApp -Exe $Exe -State $State -Environment $Environment
    $deadline = (Get-Date).AddSeconds($Timeout)
    do {
        Start-Sleep -Milliseconds 500
        $new = @(Lines | Select-Object -Skip $skip)
    } while (@($new | Where-Object { $_ -match '첫 출력 표시' }).Count -lt $Panes -and (Get-Date) -lt $deadline)
    $stop = Stop-MsmApp $app

    $appLine = $new | Where-Object { $_ -match 'App 생성' } | Select-Object -First 1
    $base = (Num $appLine) - (Plus $appLine)
    $at = { param($pattern) $l = $new | Where-Object { $_ -match $pattern } | Select-Object -First 1; if ($l) { $base + (Plus $l) } }
    $first = @($new | Where-Object { $_ -match '첫 출력 표시' } | ForEach-Object { Num $_ })
    [pscustomobject]@{
        Label     = $Label
        Run       = $run
        DotNet    = Num $appLine
        Shown     = & $at '창 표시'
        WebView2  = & $at 'WebView2 준비'
        Page      = & $at '터미널 페이지 준비'
        FirstPane = $(if ($first) { ($first | Measure-Object -Minimum).Minimum })
        AllPanes  = $(if ($first.Count -ge $Panes) { ($first | Measure-Object -Maximum).Maximum })
        Left      = $stop.Left
    }
}

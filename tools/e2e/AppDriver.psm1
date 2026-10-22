# 실제 앱을 띄우고 UI Automation 으로 조작하는 도구 모음.
# 원격 데스크톱처럼 화면이 그려지지 않는 세션에서도 돌도록 캡처는 PrintWindow 를 쓴다.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MsmWin {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

# 앱은 모니터 배율을 아는 프로세스라 실제 픽셀로 창 위치를 저장한다. 시험 쪽도 실제 픽셀로 보고 움직여야
# 125% 같은 배율에서 좌표가 어긋나지 않는다.
[MsmWin]::SetProcessDPIAware() | Out-Null
$script:UIA = [System.Windows.Automation.AutomationElement]

function Start-MsmApp {
    # Environment: 앱에만 물려줄 환경 변수(시험용 설정 폴더·토큰). 앱이 뜨면 이 셸에서는 지운다.
    # 사용자 Claude 설정으로 띄우면 시험 세션이 사용자의 훅(외부 전송 등)을 그대로 탄다. 시험용 설정 없이는 띄우지 않는다.
    param([string]$Exe, [string]$State, [int]$CdpPort = 0, [hashtable]$Environment = @{}, [switch]$UseRealConfig)
    if (-not $UseRealConfig -and -not $Environment.ContainsKey("CLAUDE_CONFIG_DIR")) {
        throw "시험용 Claude 설정이 없습니다. -Environment (Get-MsmTestEnvironment <폴더>) 를 넘기거나, 정말 사용자 설정으로 돌릴 때만 -UseRealConfig"
    }
    $env:MSM_STATE_FILE = $State
    if ($CdpPort -gt 0) { $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$CdpPort" }
    foreach ($k in $Environment.Keys) { Set-Item "Env:$k" $Environment[$k] }
    try {
        $p = Start-Process -FilePath $Exe -PassThru
    } finally {
        Remove-Item Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -ErrorAction SilentlyContinue
        foreach ($k in $Environment.Keys) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue }
    }
    $deadline = (Get-Date).AddSeconds(20)
    while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300; $p.Refresh() }
    if ($p.MainWindowHandle -eq 0) { throw "창이 뜨지 않았습니다" }
    # SW_SHOW: 최대화로 복원된 창을 되돌리지 않는다(SW_RESTORE 는 되돌린다).
    [MsmWin]::ShowWindow($p.MainWindowHandle, 5) | Out-Null
    $p
}

function Get-MsmRoot([System.Diagnostics.Process]$App) { $script:UIA::FromHandle($App.MainWindowHandle) }

function Select-MsmSession([System.Diagnostics.Process]$App, [int]$Index) {
    $cond = New-Object System.Windows.Automation.PropertyCondition ($script:UIA::ControlTypeProperty), ([System.Windows.Automation.ControlType]::ListItem)
    $items = (Get-MsmRoot $App).FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $items[$Index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

# 목록 항목마다 보이는 글(이름 · 폴더 · 상태 …)을 " | " 로 이어 돌려준다.
function Get-MsmListText([System.Diagnostics.Process]$App) {
    $item = New-Object System.Windows.Automation.PropertyCondition ($script:UIA::ControlTypeProperty), ([System.Windows.Automation.ControlType]::ListItem)
    $text = New-Object System.Windows.Automation.PropertyCondition ($script:UIA::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Text)
    foreach ($i in (Get-MsmRoot $App).FindAll([System.Windows.Automation.TreeScope]::Descendants, $item)) {
        (@($i.FindAll([System.Windows.Automation.TreeScope]::Descendants, $text) | ForEach-Object { $_.Current.Name }) -join " | ")
    }
}

# 자손 전체(자식의 자식까지). 앱이 강제로 죽은 뒤 무엇이 남았는지 보려면 미리 받아 둔다.
function Get-MsmDescendants([int]$ParentId) {
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name)
    $found = [System.Collections.Generic.List[object]]::new()
    $queue = [System.Collections.Generic.Queue[int]]::new(); $queue.Enqueue($ParentId)
    while ($queue.Count) {
        $p = $queue.Dequeue()
        foreach ($c in $all | Where-Object { $_.ParentProcessId -eq $p -and $_.ProcessId -ne $p }) { $found.Add($c); $queue.Enqueue($c.ProcessId) }
    }
    $found
}

function Invoke-MsmButton([System.Diagnostics.Process]$App, [string]$Name) {
    $cond = New-Object System.Windows.Automation.AndCondition @(
        (New-Object System.Windows.Automation.PropertyCondition ($script:UIA::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Button)),
        (New-Object System.Windows.Automation.PropertyCondition ($script:UIA::NameProperty), $Name))
    $btn = (Get-MsmRoot $App).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $btn) { throw "버튼 '$Name' 을 찾지 못했습니다" }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Save-MsmShot([System.Diagnostics.Process]$App, [string]$Path) {
    $h = $App.MainWindowHandle
    # WPF 는 활성 창이 아니면 PrintWindow 에 검은 면으로 찍힐 때가 있다. 앞으로 가져온 뒤 한 번 그리게 기다린다.
    [MsmWin]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 500
    $r = New-Object MsmWin+RECT
    [MsmWin]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [MsmWin]::PrintWindow($h, $hdc, 2) | Out-Null   # PW_RENDERFULLCONTENT: WebView2 내용까지
    $g.ReleaseHdc($hdc)
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Set-MsmWindowSize([System.Diagnostics.Process]$App, [int]$Width, [int]$Height) {
    [MsmWin]::ShowWindow($App.MainWindowHandle, 9) | Out-Null
    [MsmWin]::SetWindowPos($App.MainWindowHandle, [IntPtr]::Zero, 0, 0, $Width, $Height, 0x0016) | Out-Null  # NOMOVE|NOZORDER|NOACTIVATE
}

# 이동과 크기를 한 번에. 좌표는 화면 픽셀이다.
function Set-MsmWindowRect([System.Diagnostics.Process]$App, [int]$Left, [int]$Top, [int]$Width, [int]$Height) {
    [MsmWin]::ShowWindow($App.MainWindowHandle, 9) | Out-Null
    [MsmWin]::SetWindowPos($App.MainWindowHandle, [IntPtr]::Zero, $Left, $Top, $Width, $Height, 0x0014) | Out-Null  # NOZORDER|NOACTIVATE
}

function Get-MsmWindowRect([System.Diagnostics.Process]$App) {
    $r = New-Object MsmWin+RECT
    [MsmWin]::GetWindowRect($App.MainWindowHandle, [ref]$r) | Out-Null
    [pscustomobject]@{ Left = $r.Left; Top = $r.Top; Width = $r.Right - $r.Left; Height = $r.Bottom - $r.Top; Maximized = [MsmWin]::IsZoomed($App.MainWindowHandle) }
}

# clone_config.py 로 만든 시험용 설정과 따로 받은 토큰. 사용자의 Claude 설정·로그인은 건드리지 않는다.
function Get-MsmTestEnvironment([string]$ConfigDir, [string]$TokenFile = (Join-Path $env:USERPROFILE ".msm-e2e-token")) {
    if (-not (Test-Path $TokenFile)) { throw "시험용 토큰이 없습니다: $TokenFile (claude setup-token 으로 받아 저장)" }
    @{ CLAUDE_CONFIG_DIR = $ConfigDir; CLAUDE_CODE_OAUTH_TOKEN = (Get-Content $TokenFile -Raw).Trim() }
}

function Set-MsmMaximized([System.Diagnostics.Process]$App) { [MsmWin]::ShowWindow($App.MainWindowHandle, 3) | Out-Null }

function Get-MsmChildren([System.Diagnostics.Process]$App) {
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$($App.Id)" | Select-Object ProcessId, Name, CommandLine
}

function Stop-MsmApp([System.Diagnostics.Process]$App) {
    $kids = Get-MsmChildren $App
    $App.CloseMainWindow() | Out-Null
    if (-not $App.WaitForExit(15000)) { $App.Kill(); throw "15초 안에 끝나지 않았습니다" }
    # Job 을 닫으면 OS 가 자식을 끝내지만, PC 가 바쁘면 몇 초 걸린다. 10초까지 기다려 늦은 것과 남은 것을 가른다.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 500
        $left = @($kids | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
    } while ($left.Count -gt 0 -and $sw.Elapsed.TotalSeconds -lt 10)
    [pscustomobject]@{ Children = @($kids).Count; Left = $left.Count; LeftNames = ($left.Name -join ","); WaitMs = $sw.ElapsedMilliseconds }
}

Export-ModuleMember -Function *-Msm*

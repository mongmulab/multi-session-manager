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
  public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$script:UIA = [System.Windows.Automation.AutomationElement]

function Start-MsmApp {
    param([string]$Exe, [string]$State, [int]$CdpPort = 0)
    $env:MSM_STATE_FILE = $State
    if ($CdpPort -gt 0) { $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$CdpPort" }
    try {
        $p = Start-Process -FilePath $Exe -PassThru
    } finally {
        Remove-Item Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS -ErrorAction SilentlyContinue
    }
    $deadline = (Get-Date).AddSeconds(20)
    while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300; $p.Refresh() }
    if ($p.MainWindowHandle -eq 0) { throw "창이 뜨지 않았습니다" }
    [MsmWin]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
    $p
}

function Get-MsmRoot([System.Diagnostics.Process]$App) { $script:UIA::FromHandle($App.MainWindowHandle) }

function Select-MsmSession([System.Diagnostics.Process]$App, [int]$Index) {
    $cond = New-Object System.Windows.Automation.PropertyCondition ($script:UIA::ControlTypeProperty), ([System.Windows.Automation.ControlType]::ListItem)
    $items = (Get-MsmRoot $App).FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $items[$Index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
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

function Get-MsmChildren([System.Diagnostics.Process]$App) {
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$($App.Id)" | Select-Object ProcessId, Name, CommandLine
}

function Stop-MsmApp([System.Diagnostics.Process]$App) {
    $kids = Get-MsmChildren $App
    $App.CloseMainWindow() | Out-Null
    if (-not $App.WaitForExit(15000)) { $App.Kill(); throw "15초 안에 끝나지 않았습니다" }
    Start-Sleep -Seconds 2
    $left = @($kids | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
    [pscustomobject]@{ Children = $kids.Count; Left = $left.Count }
}

Export-ModuleMember -Function *-Msm*

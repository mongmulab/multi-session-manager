param([int]$AppPid)
# 앱 본체 / WebView2 프로세스 묶음 / 자식 claude·conhost 를 나눠 Private Working Set(MB)을 잰다.
$all = Get-CimInstance Win32_Process
function Desc([int]$root) {
    $ids = @($root); $queue = @($root)
    while ($queue.Count) {
        $next = @()
        foreach ($q in $queue) { $next += @($all | Where-Object { $_.ParentProcessId -eq $q -and $_.ProcessId -ne $q } | ForEach-Object { [int]$_.ProcessId }) }
        $ids += $next; $queue = $next
    }
    $ids
}
$tree = Desc $AppPid
$rows = foreach ($id in $tree) {
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($p) { [pscustomobject]@{ Pid = $id; Name = $p.ProcessName; PrivateMB = [math]::Round($p.PrivateMemorySize64 / 1MB, 1); WsMB = [math]::Round($p.WorkingSet64 / 1MB, 1) } }
}
$rows | Sort-Object Name | Format-Table -AutoSize | Out-String | Write-Output
$rows | Group-Object Name | ForEach-Object { "{0,-20} n={1} private={2}MB ws={3}MB" -f $_.Name, $_.Count, [math]::Round(($_.Group | Measure-Object PrivateMB -Sum).Sum, 1), [math]::Round(($_.Group | Measure-Object WsMB -Sum).Sum, 1) }

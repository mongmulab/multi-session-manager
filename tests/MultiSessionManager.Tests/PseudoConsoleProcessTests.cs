using System.Diagnostics;
using System.Text;
using MultiSessionManager.Pty;

namespace MultiSessionManager.Tests;

/// <summary>실제 ConPTY 를 띄워 보는 시험. Windows 10 1809 이상에서만 돈다.</summary>
public class PseudoConsoleProcessTests
{
    private static readonly string Cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    private static PtyStartInfo Info(params string[] args) =>
        new(Cmd, args, Path.GetTempPath(), EnvironmentBlock.FromCurrentProcess(), 100, 30);

    private sealed class Capture
    {
        private readonly StringBuilder _sb = new();
        public readonly TaskCompletionSource<int> Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Add(string s) { lock (_sb) _sb.Append(s); }
        public string Text { get { lock (_sb) return _sb.ToString(); } }

        public async Task<bool> WaitFor(string needle, int ms = 10000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (Text.Contains(needle)) return true;
                await Task.Delay(50);
            }
            return false;
        }
    }

    private static (PseudoConsoleProcess, Capture) Start(PtyStartInfo info, ProcessJob? job = null)
    {
        var cap = new Capture();
        var p = PseudoConsoleProcess.Start(info, job);
        p.Output += cap.Add;
        p.Exited += code => cap.Exit.TrySetResult(code);
        return (p, cap);
    }

    [Fact]
    public async Task Output_and_exit_code_arrive_through_conpty()
    {
        var (p, cap) = Start(Info("/d", "/c", "echo pty-ok&& exit /b 7"));
        using (p)
        {
            var code = await cap.Exit.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(7, code);
            // Exited 는 남은 출력을 다 보낸 다음에 온다.
            Assert.Contains("pty-ok", cap.Text);
        }
    }

    [Fact]
    public async Task Typed_input_reaches_the_child_and_korean_comes_back_intact()
    {
        var (p, cap) = Start(Info("/d", "/k", "chcp 65001>nul"));
        using (p)
        {
            Assert.True(await cap.WaitFor(">"), "프롬프트가 떠야 한다: " + cap.Text);
            p.Write("echo 한글-입출력\r");
            Assert.True(await cap.WaitFor("한글-입출력\r\n"), "한글이 그대로 돌아와야 한다: " + cap.Text);
            p.Resize(60, 20);
            p.Write("exit\r");
            Assert.Equal(0, await cap.Exit.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        }
    }

    [Fact]
    public async Task Disposing_the_job_kills_grandchildren()
    {
        int grandchildPid;
        var marker = "msm-job-" + Guid.NewGuid().ToString("N")[..8];
        using (var job = new ProcessJob())
        {
            // cmd 가 ping 을 띄우면 ping 은 손자다. Job 을 닫으면 둘 다 끝나야 한다.
            var (p, cap) = Start(Info("/d", "/c", $"title {marker}&& ping -n 60 127.0.0.1"), job);
            Assert.True(await cap.WaitFor("127.0.0.1"), "ping 이 시작돼야 한다: " + cap.Text);
            grandchildPid = FindChild(p.ProcessId, "PING.EXE");
            Assert.True(grandchildPid > 0, "손자 프로세스를 찾지 못했다");
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5) && IsAlive(grandchildPid))
            await Task.Delay(100);
        Assert.False(IsAlive(grandchildPid), "Job 을 닫았는데 손자 프로세스가 남았다");
    }

    [Fact]
    public void Job_waits_until_its_processes_are_gone()
    {
        using var job = new ProcessJob();
        // 콘솔을 닫은 뒤에도 남는 자손(Claude 가 끝나며 띄운 훅 같은 것)의 자리. 2초쯤 돌고 끝난다.
        using var ping = Process.Start(new ProcessStartInfo("ping", "-n 3 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        job.Assign(ping.Handle);
        Assert.Equal(1, job.ActiveProcesses);
        Assert.False(job.WaitUntilEmpty(TimeSpan.FromMilliseconds(200)));
        Assert.True(job.WaitUntilEmpty(TimeSpan.FromSeconds(10)), "ping 이 끝나면 Job 이 비어야 한다");
        Assert.Equal(0, job.ActiveProcesses);
    }

    [Fact]
    public async Task Kill_reports_exit()
    {
        var (p, cap) = Start(Info("/d", "/c", "ping -n 60 127.0.0.1"));
        using (p)
        {
            Assert.True(await cap.WaitFor("127.0.0.1"));
            p.Kill();
            Assert.Equal(1, await cap.Exit.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [NodeFact]
    public async Task Dispose_lets_the_child_clean_up_before_ending()
    {
        // 콘솔이 닫히면 node 는 SIGHUP 을 받는다. 강제로 죽였다면 표시 파일이 생기지 않는다.
        var dir = Path.Combine(Path.GetTempPath(), "msm-hup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var marker = Path.Combine(dir, "closed.txt");
            var script = Path.Combine(dir, "hup.js");
            File.WriteAllText(script, $$"""
                process.on('SIGHUP', () => { require('fs').writeFileSync({{System.Text.Json.JsonSerializer.Serialize(marker)}}, 'hup'); process.exit(0); });
                console.log('hup-ready');
                setInterval(() => {}, 1000);
                """);
            var (p, cap) = Start(new PtyStartInfo(NodeFactAttribute.Node!, [script], dir, EnvironmentBlock.FromCurrentProcess(), 100, 30));
            Assert.True(await cap.WaitFor("hup-ready"), "node 가 떠야 한다: " + cap.Text);

            var sw = Stopwatch.StartNew();
            p.Dispose();
            Assert.True(File.Exists(marker), "자식이 정리할 기회를 얻지 못했다");
            Assert.True(sw.Elapsed < PseudoConsoleProcess.GracefulExitTimeout + TimeSpan.FromSeconds(3), $"끝내는 데 {sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [NodeFact]
    public async Task Dispose_sends_the_quit_input_before_closing_the_console()
    {
        // Claude 처럼 키를 날것으로 읽다가 Ctrl+C()가 오면 나간다. 콘솔을 먼저 닫았다면 SIGHUP 이 먼저 온다.
        var dir = Path.Combine(Path.GetTempPath(), "msm-quit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var marker = Path.Combine(dir, "how.txt");
            var script = Path.Combine(dir, "quit.js");
            var m = System.Text.Json.JsonSerializer.Serialize(marker);
            File.WriteAllText(script, $$"""
                const fs = require('fs');
                process.stdin.setRawMode(true);
                process.stdin.on('data', (d) => { if (d.includes(3)) { fs.writeFileSync({{m}}, 'ctrl-c'); process.exit(0); } });
                process.on('SIGHUP', () => { fs.writeFileSync({{m}}, 'hup'); process.exit(0); });
                console.log('quit-ready');
                setInterval(() => {}, 1000);
                """);
            var (p, cap) = Start(new PtyStartInfo(NodeFactAttribute.Node!, [script], dir, EnvironmentBlock.FromCurrentProcess(), 100, 30, [""]));
            Assert.True(await cap.WaitFor("quit-ready"), "node 가 떠야 한다: " + cap.Text);

            p.Dispose();
            Assert.Equal("ctrl-c", File.Exists(marker) ? File.ReadAllText(marker) : "(없음)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Dispose_still_ends_a_child_that_ignores_the_close()
    {
        // ping 은 CTRL_CLOSE 를 따로 다루지 않는다. 기다리는 시간이 지나면 강제로라도 끝나야 한다.
        var (p, cap) = Start(Info("/d", "/c", "ping -n 60 127.0.0.1"));
        Assert.True(await cap.WaitFor("127.0.0.1"));
        var pid = p.ProcessId;
        var sw = Stopwatch.StartNew();
        p.Dispose();
        Assert.False(IsAlive(pid));
        Assert.True(sw.Elapsed < PseudoConsoleProcess.GracefulExitTimeout + TimeSpan.FromSeconds(6), $"끝내는 데 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Missing_executable_throws_clear_error()
    {
        var info = new PtyStartInfo(@"C:\nope\missing.exe", [], Path.GetTempPath(), EnvironmentBlock.FromCurrentProcess());
        var ex = Assert.Throws<System.ComponentModel.Win32Exception>(() => PseudoConsoleProcess.Start(info));
        Assert.Contains("missing.exe", ex.Message);
        Assert.Equal(2, ex.NativeErrorCode); // ERROR_FILE_NOT_FOUND
    }

    private static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static int FindChild(int parentPid, string imageName)
    {
        // 표준 라이브러리에 부모 PID 조회가 없어 Toolhelp 스냅숏으로 찾는다.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            foreach (var (pid, ppid, name) in Toolhelp.Processes())
                if (ppid == parentPid && name.Equals(imageName, StringComparison.OrdinalIgnoreCase))
                    return pid;
            Thread.Sleep(100);
        }
        return 0;
    }
}

/// <summary>PATH 에 node 가 있을 때만 돈다. 콘솔 닫힘 신호를 받아 정리하는 자식이 필요해서 쓴다.</summary>
public sealed class NodeFactAttribute : FactAttribute
{
    public static readonly string? Node = (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(d => Path.Combine(d.Trim(), "node.exe"))
        .FirstOrDefault(File.Exists);

    public NodeFactAttribute()
    {
        if (Node is null) Skip = "PATH 에 node.exe 가 없습니다";
    }
}

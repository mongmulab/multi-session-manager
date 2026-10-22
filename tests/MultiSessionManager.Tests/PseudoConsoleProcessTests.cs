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

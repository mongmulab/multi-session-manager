using System.Text;
using MultiSessionManager.Agents;
using MultiSessionManager.Pty;
using MultiSessionManager.Sessions;

namespace MultiSessionManager.Tests;

/// <summary>세션 시작을 스레드풀로 옮기면서 생긴 경계: 첫 출력, 아주 빨리 끝나는 프로세스, 중복 시작.</summary>
public class SessionStartTests
{
    private sealed class CmdAdapter(string script) : IAgentAdapter
    {
        public string Kind => "cmd";
        public int Prepared;

        public AgentLaunch Prepare(SessionRecord session, int columns, int rows)
        {
            Interlocked.Increment(ref Prepared);
            var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            return new AgentLaunch(new PtyStartInfo(cmd, ["/d", "/c", script], session.WorkingDirectory, EnvironmentBlock.FromCurrentProcess(), columns, rows), Resumes: false);
        }

        public bool HasConversation(SessionRecord session) => false;
    }

    private static Session NewSession() => new(new SessionRecord { Title = "t", WorkingDirectory = Path.GetTempPath() });

    [Fact]
    public async Task First_output_is_not_lost()
    {
        using var job = new ProcessJob();
        var session = NewSession();
        var sb = new StringBuilder();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Output += (_, text) => { lock (sb) sb.Append(text); };
        session.Exited += (_, code) => exited.TrySetResult(code);

        await session.StartAsync(new CmdAdapter("echo first-line"), job, 80, 24);
        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        lock (sb) Assert.Contains("first-line", sb.ToString());
    }

    [Fact]
    public async Task Process_that_exits_immediately_ends_as_exited_not_running()
    {
        using var job = new ProcessJob();
        // 같은 경계를 여러 번 밟아 본다. 띄우는 사이에 끝나는 경우가 섞이게 된다.
        for (var i = 0; i < 10; i++)
        {
            var session = NewSession();
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Exited += (_, code) => exited.TrySetResult(code);
            await session.StartAsync(new CmdAdapter("exit 3"), job, 80, 24);
            Assert.Equal(3, await exited.Task.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(SessionStatus.Exited, session.Status);
            Assert.Equal("종료됨 (3)", session.StatusText);
        }
    }

    [Fact]
    public async Task Second_start_while_starting_is_ignored()
    {
        using var job = new ProcessJob();
        var session = NewSession();
        var adapter = new CmdAdapter("ping -n 30 127.0.0.1 >nul");
        var first = session.StartAsync(adapter, job, 80, 24);
        Assert.Equal(SessionStatus.Starting, session.Status);
        var second = await session.StartAsync(adapter, job, 80, 24);

        Assert.Null(second);
        Assert.False(await first);
        Assert.Equal(1, adapter.Prepared);
        Assert.Equal(SessionStatus.Running, session.Status);
        session.Stop();
        Assert.Equal(SessionStatus.Idle, session.Status);
    }

    [Fact]
    public async Task Failed_start_reports_failure()
    {
        using var job = new ProcessJob();
        var session = new Session(new SessionRecord { Title = "t", WorkingDirectory = @"C:\definitely\missing\folder" });
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => session.StartAsync(new CmdAdapter("echo x"), job, 80, 24));
        Assert.Equal(SessionStatus.Failed, session.Status);
        // 사용자가 무엇이 문제인지 알 수 있도록 폴더와 Windows 의 사유가 함께 나온다.
        Assert.Contains(@"C:\definitely\missing\folder", ex.Message);
        Assert.Contains(" — ", ex.Message);
        Assert.Contains(@"C:\definitely\missing\folder", session.StatusText);
    }
}

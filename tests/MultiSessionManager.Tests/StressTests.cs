using System.Diagnostics;
using MultiSessionManager.Agents;
using MultiSessionManager.Pty;
using MultiSessionManager.Sessions;
using Xunit.Abstractions;

namespace MultiSessionManager.Tests;

/// <summary>세션을 수없이 열고 닫아도 프로세스·핸들·스레드가 쌓이지 않는지(4단계 수용 기준: 100회 생성·삭제).</summary>
public sealed class StressTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msm-stress-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // 끝나지 않고 입력을 기다리는 셸. 실제 CLI 처럼 닫을 때까지 살아 있다.
    private sealed class ShellAdapter : IAgentAdapter
    {
        public string Kind => "cmd";

        public AgentLaunch Prepare(SessionRecord session, int columns, int rows)
        {
            var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            return new AgentLaunch(new PtyStartInfo(cmd, ["/d", "/k", "echo ready-" + session.Id], session.WorkingDirectory, EnvironmentBlock.FromCurrentProcess(), columns, rows), Resumes: false);
        }

        public bool HasConversation(SessionRecord session) => false;
    }

    private static (int Handles, int Threads) Measure()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var self = Process.GetCurrentProcess();
        return (self.HandleCount, self.Threads.Count);
    }

    [Fact]
    public async Task Hundred_create_start_close_cycles_leave_nothing_behind()
    {
        Directory.CreateDirectory(_dir);
        using var manager = new SessionManager(new SessionStore(Path.Combine(_dir, "sessions.json")), new ShellAdapter());
        manager.Load();

        // 처음 몇 번은 스레드풀·ConPTY 초기화로 핸들이 는다. 그 뒤를 기준으로 삼는다.
        async Task Cycle()
        {
            var session = manager.Create(_dir);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Output += (_, text) => { if (text.Contains("ready-" + session.Id)) ready.TrySetResult(); };
            await manager.StartAsync(session, 80, 24);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            manager.Remove(session);
        }

        for (var i = 0; i < 5; i++) await Cycle();
        var before = Measure();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++) await Cycle();
        var after = Measure();

        output.WriteLine($"100회 {sw.Elapsed.TotalSeconds:F1}초 · 핸들 {before.Handles}→{after.Handles} · 스레드 {before.Threads}→{after.Threads}");
        Assert.Empty(manager.Sessions);
        Assert.Equal(0, manager.ActiveProcesses);
        // 한 번에 파이프 2·프로세스·이벤트·ConPTY 핸들을 쓴다. 새고 있다면 100회에 수백 개가 남는다.
        Assert.True(after.Handles - before.Handles < 50, $"핸들이 {after.Handles - before.Handles}개 늘었다");
        Assert.True(after.Threads - before.Threads < 10, $"스레드가 {after.Threads - before.Threads}개 늘었다");
    }
}

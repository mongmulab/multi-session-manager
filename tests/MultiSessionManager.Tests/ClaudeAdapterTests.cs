using MultiSessionManager.Agents;
using MultiSessionManager.Pty;
using MultiSessionManager.Sessions;

namespace MultiSessionManager.Tests;

public sealed class ClaudeAdapterTests : IDisposable
{
    private readonly string _projects = Path.Combine(Path.GetTempPath(), "msm-test-" + Guid.NewGuid().ToString("N"));

    public ClaudeAdapterTests() => Directory.CreateDirectory(_projects);

    public void Dispose() => Directory.Delete(_projects, recursive: true);

    private ClaudeAdapter Adapter(string exe = @"C:\tools\claude.exe", Dictionary<string, string>? env = null) =>
        new(exe, _projects, () => new Dictionary<string, string>(env ?? new() { ["PATH"] = @"C:\Windows" }, StringComparer.OrdinalIgnoreCase));

    private static SessionRecord Record(string title = "백엔드") =>
        new() { Title = title, WorkingDirectory = @"D:\work\api" };

    private void WriteTranscript(SessionRecord record, string content)
    {
        var dir = Path.Combine(_projects, "D--work-api");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, record.AgentSessionId + ".jsonl"), content);
    }

    [Fact]
    public void New_session_uses_session_id_chosen_by_app()
    {
        var record = Record();
        var args = Adapter().BuildArguments(record);
        Assert.Equal(["--session-id", record.AgentSessionId, "--name", "백엔드"], args);
        Assert.True(Guid.TryParse(record.AgentSessionId, out _), "Claude Code 는 UUID 만 받는다");
    }

    [Fact]
    public void Existing_transcript_switches_to_resume()
    {
        var record = Record();
        WriteTranscript(record, "{\"type\":\"user\"}\n");
        var args = Adapter().BuildArguments(record);
        Assert.Equal(["--resume", record.AgentSessionId, "--name", "백엔드"], args);
    }

    [Fact]
    public void Empty_transcript_is_not_resumable()
    {
        // 띄우기만 하고 아무 말도 안 하면 파일이 비어 있고, 그걸 --resume 하면 CLI 가 실패한다.
        var record = Record();
        WriteTranscript(record, "");
        Assert.Equal("--session-id", Adapter().BuildArguments(record)[0]);
    }

    [Fact]
    public void Transcript_in_another_project_folder_is_found()
    {
        // 폴더 이름 규칙을 흉내 내지 않고 파일 이름으로 찾는다.
        var record = Record();
        var dir = Path.Combine(_projects, "C--somewhere-else");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, record.AgentSessionId + ".jsonl"), "x");
        Assert.True(Adapter().HasConversation(record));
    }

    [Fact]
    public void Blank_title_omits_name_flag()
    {
        var args = Adapter().BuildArguments(Record(title: " "));
        Assert.DoesNotContain("--name", args);
    }

    [Fact]
    public void Outer_claude_session_variables_are_removed()
    {
        var env = new Dictionary<string, string>
        {
            ["PATH"] = @"C:\Windows",
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_ENTRYPOINT"] = "cli",
            ["CLAUDE_CODE_SESSION_ID"] = "outer",
            ["CLAUDE_CODE_GIT_BASH_PATH"] = @"C:\Git\bin\bash.exe",
        };
        var info = Adapter(env: env).BuildStartInfo(Record(), 100, 40);

        Assert.False(info.Environment.ContainsKey("CLAUDECODE"));
        Assert.False(info.Environment.ContainsKey("CLAUDE_CODE_SESSION_ID"));
        Assert.Equal(@"C:\Git\bin\bash.exe", info.Environment["CLAUDE_CODE_GIT_BASH_PATH"]);
        Assert.Equal("xterm-256color", info.Environment["TERM"]);
        Assert.Equal((100, 40), (info.Columns, info.Rows));
        Assert.Equal(@"D:\work\api", info.WorkingDirectory);
    }

    [Fact]
    public void Npm_shim_runs_through_cmd()
    {
        var info = Adapter(exe: @"C:\npm\claude.cmd").BuildStartInfo(Record(), 80, 24);
        Assert.EndsWith("cmd.exe", info.ApplicationPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/d", "/s", "/c"], info.Arguments.Take(3));
        Assert.StartsWith(@"C:\npm\claude.cmd --session-id ", info.Arguments[3]);
    }

    [Fact]
    public void Locator_prefers_exe_over_cmd()
    {
        var a = Path.Combine(_projects, "a");
        var b = Path.Combine(_projects, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "claude.cmd"), "");
        File.WriteAllText(Path.Combine(b, "claude.exe"), "");
        var found = ExecutableLocator.Find("claude", a + Path.PathSeparator + b);
        Assert.Equal(Path.Combine(b, "claude.exe"), found);
    }

    [Fact]
    public void Environment_block_is_sorted_and_double_terminated()
    {
        var block = EnvironmentBlock.Build(new Dictionary<string, string> { ["b"] = "2", ["A"] = "1", ["bad=key"] = "x" });
        Assert.Equal("A=1\0b=2\0\0", block);
    }
}

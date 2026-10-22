using System.Net;
using System.Net.Http;
using System.Text;
using MultiSessionManager.Agents;
using MultiSessionManager.Sessions;

namespace MultiSessionManager.Tests;

public sealed class HookServerTests : IDisposable
{
    private readonly HookServer _server = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public void Dispose() => _server.Dispose();

    [Theory]
    [InlineData("UserPromptSubmit", null, AgentActivity.Working)]
    [InlineData("PostToolUse", null, AgentActivity.Working)]
    [InlineData("Stop", null, AgentActivity.Waiting)]
    [InlineData("StopFailure", null, AgentActivity.Failed)]
    [InlineData("PermissionRequest", null, AgentActivity.NeedsInput)]
    [InlineData("Notification", "permission_prompt", AgentActivity.NeedsInput)]
    [InlineData("Notification", "idle_prompt", AgentActivity.Waiting)]
    public void Hook_events_map_to_activity(string hookEvent, string? kind, AgentActivity expected) =>
        Assert.Equal(expected, HookServer.Map(hookEvent, kind));

    [Theory]
    [InlineData("SessionStart", null)]
    [InlineData("Notification", "auth_success")]
    [InlineData(null, null)]
    public void Unrelated_events_change_nothing(string? hookEvent, string? kind) =>
        Assert.Null(HookServer.Map(hookEvent, kind));

    private async Task<(HttpStatusCode Status, (string Id, AgentActivity Activity)? Got)> Post(string url, string json)
    {
        var got = new TaskCompletionSource<(string, AgentActivity)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(string id, AgentActivity a) => got.TrySetResult((id, a));
        _server.Received += On;
        try
        {
            using var response = await Http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            var done = await Task.WhenAny(got.Task, Task.Delay(500));
            return (response.StatusCode, done == got.Task ? got.Task.Result : null);
        }
        finally
        {
            _server.Received -= On;
        }
    }

    [Fact]
    public async Task Post_from_claude_reaches_the_session()
    {
        var (status, got) = await Post(_server.UrlFor("s 1"), """{"session_id":"x","hook_event_name":"Stop"}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("s 1", AgentActivity.Waiting), got);
    }

    [Fact]
    public async Task Large_tool_result_body_is_read()
    {
        // PostToolUse 에는 도구 결과가 통째로 실린다.
        var big = new string('가', 2_000_000);
        var (status, got) = await Post(_server.UrlFor("s0"), $$"""{"hook_event_name":"PostToolUse","tool_response":"{{big}}"}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(AgentActivity.Working, got?.Activity);
    }

    [Fact]
    public async Task Wrong_token_is_refused()
    {
        var url = new Uri(_server.UrlFor("s0"));
        var forged = $"http://127.0.0.1:{url.Port}/hook/{new string('0', 32)}/s0";
        var (status, got) = await Post(forged, """{"hook_event_name":"Stop"}""");
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Null(got);
    }

    [Fact]
    public async Task Broken_json_is_ignored_and_server_keeps_working()
    {
        var (status, got) = await Post(_server.UrlFor("s0"), "{not json");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Null(got);
        (status, got) = await Post(_server.UrlFor("s0"), """{"hook_event_name":"UserPromptSubmit"}""");
        Assert.Equal(AgentActivity.Working, got?.Activity);
    }

    [Theory]
    [InlineData(AgentActivity.NeedsInput, "\u001b", AgentActivity.Waiting)]   // 확인 화면을 Esc 로 거절
    [InlineData(AgentActivity.Working, "\u001b", AgentActivity.Waiting)]      // 일하는 중에 Esc 로 끊음
    [InlineData(AgentActivity.Working, "\u0003", AgentActivity.Waiting)]      // Ctrl+C 로 끊음
    [InlineData(AgentActivity.NeedsInput, "1", AgentActivity.Unknown)]        // 답을 골랐다. 결과는 다음 Hook 이 알려 준다
    [InlineData(AgentActivity.NeedsInput, "\r", AgentActivity.Unknown)]
    [InlineData(AgentActivity.Working, "\u001b[A", AgentActivity.Working)]    // 화살표 키는 끊는 것이 아니다
    [InlineData(AgentActivity.Waiting, "\u001b", AgentActivity.Waiting)]
    [InlineData(AgentActivity.Waiting, "abc", AgentActivity.Waiting)]
    public void Interrupting_keys_end_the_turn(AgentActivity before, string input, AgentActivity after) =>
        Assert.Equal(after, Session.AfterInput(before, input));

    [Fact]
    public void Session_ignores_reports_when_not_running()
    {
        var session = new Session(new SessionRecord { Title = "t", WorkingDirectory = "D:/w" });
        session.ReportActivity(AgentActivity.Working);
        Assert.Equal(AgentActivity.Unknown, session.Activity);
        Assert.Equal("대기", session.StatusText);
    }

    [Fact]
    public void Adapter_passes_hook_settings_without_touching_user_settings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "msm-hooks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var adapter = new ClaudeAdapter(@"C:\tools\claude.exe", dir, () => new(StringComparer.OrdinalIgnoreCase)) { Hooks = _server, HookSettingsDirectory = dir };
            var record = new SessionRecord { Title = "api", WorkingDirectory = @"D:\work\api" };
            var args = adapter.Prepare(record, 80, 24).StartInfo.Arguments.ToList();
            var i = args.IndexOf("--settings");
            Assert.True(i >= 0, string.Join(" ", args));
            var json = File.ReadAllText(args[i + 1]);
            Assert.Contains(_server.UrlFor(record.Id), json);
            foreach (var e in ClaudeAdapter.HookEvents)
                Assert.Contains($"\"{e}\"", json);
            Assert.Contains("\"type\":\"http\"", json);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}

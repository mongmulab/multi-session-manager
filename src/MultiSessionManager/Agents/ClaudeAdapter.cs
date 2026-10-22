using MultiSessionManager.Pty;
using MultiSessionManager.Sessions;

namespace MultiSessionManager.Agents;

/// <summary>
/// Claude Code CLI 어댑터.
/// 세션 ID 는 앱이 먼저 정해 <c>--session-id</c> 로 넘기고, 다음 기동 때 같은 ID 로 <c>--resume</c> 한다.
/// CLI 출력을 읽어 ID 를 알아낼 필요가 없어진다.
/// </summary>
public sealed class ClaudeAdapter : IAgentAdapter
{
    // 이 앱을 Claude Code 안에서 띄웠을 때 물려받는, 바깥 세션에 묶인 값. 남겨 두면 자식 claude 가
    // 자기를 중첩 실행이나 바깥 세션의 일부로 여긴다. 사용자 설정용 CLAUDE_CODE_* (예: GIT_BASH_PATH)는 건드리지 않는다.
    internal static readonly string[] StrippedVariables =
    [
        "CLAUDECODE", "AI_AGENT", "CLAUDE_PID", "CLAUDE_EFFORT",
        "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT", "CLAUDE_CODE_EXECPATH",
        "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_SESSION_ATTENDED",
        "CLAUDE_CODE_BRIDGE_SESSION_ID", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN",
    ];

    private readonly string _executable;
    private readonly string _projectsDirectory;
    private readonly Func<Dictionary<string, string>> _baseEnvironment;

    public string Kind => "claude";

    /// <summary>
    /// 상태 알림을 받을 곳. 있으면 세션마다 Hook 설정 파일을 만들어 <c>--settings</c> 로 넘긴다.
    /// 사용자의 settings.json 은 고치지 않는다. Claude 는 이 Hook 을 사용자 Hook 에 더해서 돌린다(바꿔치지 않는다).
    /// </summary>
    public HookServer? Hooks { get; set; }

    /// <summary>세션별 Hook 설정 파일을 두는 곳.</summary>
    public string HookSettingsDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiSessionManager", "hooks");

    // 상태에 쓰는 Hook 만 건다. 짧게 끊어, 앱이 응답하지 못해도 Claude 가 기다리지 않게 한다.
    internal static readonly string[] HookEvents =
        ["UserPromptSubmit", "PostToolUse", "PostToolUseFailure", "Stop", "StopFailure", "PermissionRequest", "Notification"];

    internal static string HookSettingsJson(string url)
    {
        var hook = new[] { new { type = "http", url, timeout = 3 } };
        var hooks = HookEvents.ToDictionary(e => e, e => (object)(e.StartsWith("PostToolUse") ? new object[] { new { matcher = "*", hooks = hook } } : new object[] { new { hooks = hook } }));
        return System.Text.Json.JsonSerializer.Serialize(new { hooks });
    }

    public ClaudeAdapter(string executable, string? projectsDirectory = null, Func<Dictionary<string, string>>? baseEnvironment = null)
    {
        _executable = executable;
        _projectsDirectory = projectsDirectory ?? DefaultProjectsDirectory();
        _baseEnvironment = baseEnvironment ?? EnvironmentBlock.FromCurrentProcess;
    }

    public static string DefaultProjectsDirectory()
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrEmpty(configDir))
            configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(configDir, "projects");
    }

    public static ClaudeAdapter? TryCreate()
    {
        var path = ExecutableLocator.Find("claude");
        return path is null ? null : new ClaudeAdapter(path);
    }

    public IReadOnlyList<string> BuildArguments(SessionRecord session) => BuildArguments(session, HasConversation(session));

    private static IReadOnlyList<string> BuildArguments(SessionRecord session, bool resumes)
    {
        var args = new List<string>();
        if (resumes)
            args.AddRange(["--resume", session.AgentSessionId]);
        else
            args.AddRange(["--session-id", session.AgentSessionId]);

        if (!string.IsNullOrWhiteSpace(session.Title))
            args.AddRange(["--name", session.Title]);
        return args;
    }

    public PtyStartInfo BuildStartInfo(SessionRecord session, int columns, int rows) => Prepare(session, columns, rows).StartInfo;

    public AgentLaunch Prepare(SessionRecord session, int columns, int rows)
    {
        var resumes = HasConversation(session);
        var env = _baseEnvironment();
        foreach (var name in StrippedVariables)
            env.Remove(name);
        // 터미널 색·유니코드 처리를 xterm.js 기준으로 맞춘다.
        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";

        var args = BuildArguments(session, resumes).ToList();
        if (Hooks is not null)
        {
            // 포트가 실행마다 바뀌므로 띄울 때마다 새로 쓴다.
            Directory.CreateDirectory(HookSettingsDirectory);
            var path = Path.Combine(HookSettingsDirectory, session.Id + ".json");
            File.WriteAllText(path, HookSettingsJson(Hooks.UrlFor(session.Id)));
            args.AddRange(["--settings", path]);
        }
        // Ctrl+C: 입력 중이면 지우고, 답하는 중이면 멈추고, 빈 입력에서 두 번이면 나간다. 세 번이면 어느 경우든 나간다.
        IReadOnlyList<string> quit = ["\u0003", "\u0003", "\u0003"];
        if (ExecutableLocator.NeedsCmdShell(_executable))
        {
            var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            // cmd /s /c "...": 바깥 따옴표 한 쌍만 벗기고 나머지는 그대로 둔다.
            var inner = CommandLine.Build(_executable, args);
            return new AgentLaunch(new PtyStartInfo(comspec, ["/d", "/s", "/c", inner], session.WorkingDirectory, env, columns, rows, quit), resumes);
        }
        return new AgentLaunch(new PtyStartInfo(_executable, args, session.WorkingDirectory, env, columns, rows, quit), resumes);
    }

    public bool HasConversation(SessionRecord session) =>
        ClaudeTranscriptLocator.Find(_projectsDirectory, session.AgentSessionId) is not null;
}

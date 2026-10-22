namespace MultiSessionManager.Sessions;

/// <summary>디스크에 남기는 세션 한 건. 실행 중인 프로세스 정보는 여기 넣지 않는다.</summary>
public sealed class SessionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string Agent { get; set; } = "claude";

    /// <summary>CLI 쪽 대화 ID. Claude Code 는 UUID 여야 한다.</summary>
    public string AgentSessionId { get; set; } = Guid.NewGuid().ToString();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastStartedAt { get; set; }
}

public sealed class AppState
{
    public int Version { get; set; } = 1;
    public List<SessionRecord> Sessions { get; set; } = [];
    public string? ActiveSessionId { get; set; }
    public PaneLayout? Layout { get; set; }
    public WindowBounds? Window { get; set; }
}

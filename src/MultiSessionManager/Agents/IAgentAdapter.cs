using MultiSessionManager.Pty;
using MultiSessionManager.Sessions;

namespace MultiSessionManager.Agents;

/// <summary>
/// CLI 마다 다른 시작 인자·재개 방식을 감춘다. 지금은 Claude Code 하나뿐이고,
/// Codex·Gemini 는 이 자리에 구현을 하나씩 더 붙인다.
/// </summary>
public interface IAgentAdapter
{
    string Kind { get; }

    /// <summary>이 세션을 띄울 명령. 처음이면 새 대화, 기록이 있으면 이어 가기.</summary>
    PtyStartInfo BuildStartInfo(SessionRecord session, int columns, int rows);

    /// <summary>재개할 대화 기록이 디스크에 남아 있는지.</summary>
    bool HasConversation(SessionRecord session);
}

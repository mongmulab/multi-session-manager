namespace MultiSessionManager.Agents;

/// <summary>
/// Claude Code 는 대화를 <c>~/.claude/projects/&lt;작업폴더를 바꾼 이름&gt;/&lt;세션ID&gt;.jsonl</c> 에 남긴다.
/// 폴더 이름 규칙을 흉내 내면 한글·특수문자 경로에서 어긋날 수 있어서, 모든 프로젝트 폴더에서 파일 이름으로 찾는다.
/// </summary>
public static class ClaudeTranscriptLocator
{
    public static string? Find(string projectsDirectory, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !Directory.Exists(projectsDirectory))
            return null;
        if (sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        var fileName = sessionId + ".jsonl";
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(projectsDirectory))
            {
                var candidate = Path.Combine(dir, fileName);
                // 세션을 띄우고 아무것도 보내지 않으면 파일이 없거나 비어 있다. 그 상태로 --resume 하면 실패한다.
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 0)
                    return candidate;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}

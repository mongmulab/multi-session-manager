using System.Collections.ObjectModel;
using MultiSessionManager.Agents;
using MultiSessionManager.Pty;

namespace MultiSessionManager.Sessions;

/// <summary>세션 목록의 생성·이름 변경·삭제·저장을 맡는다. UI 스레드에서만 부른다.</summary>
public sealed class SessionManager : IDisposable
{
    private readonly SessionStore _store;
    private readonly IAgentAdapter _adapter;
    private readonly ProcessJob _job = new();

    public ObservableCollection<Session> Sessions { get; } = [];
    public string? ActiveSessionId { get; set; }

    public SessionManager(SessionStore store, IAgentAdapter adapter)
    {
        _store = store;
        _adapter = adapter;
    }

    public void Load()
    {
        var state = _store.Load();
        foreach (var record in state.Sessions)
            Sessions.Add(new Session(record));
        ActiveSessionId = state.ActiveSessionId;
    }

    public Session Create(string workingDirectory, string? title = null)
    {
        var record = new SessionRecord
        {
            WorkingDirectory = workingDirectory,
            Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle(workingDirectory) : title.Trim(),
        };
        var session = new Session(record);
        Sessions.Add(session);
        Save();
        return session;
    }

    public void Rename(Session session, string title)
    {
        title = title.Trim();
        if (title.Length == 0) return;
        session.Title = title;
        Save();
    }

    public void Remove(Session session)
    {
        session.Stop();
        Sessions.Remove(session);
        if (ActiveSessionId == session.Id)
            ActiveSessionId = Sessions.FirstOrDefault()?.Id;
        Save();
    }

    public void Start(Session session, int columns, int rows)
    {
        session.Start(_adapter, _job, columns, rows);
        Save();
    }

    public bool HasConversation(Session session) => _adapter.HasConversation(session.Record);

    public void Save() =>
        _store.Save(new AppState
        {
            Sessions = Sessions.Select(s => s.Record).ToList(),
            ActiveSessionId = ActiveSessionId,
        });

    private string DefaultTitle(string workingDirectory)
    {
        var name = Path.GetFileName(workingDirectory.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name)) name = workingDirectory;
        var taken = Sessions.Select(s => s.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name} ({i})"))
                return $"{name} ({i})";
    }

    public void Dispose()
    {
        foreach (var session in Sessions)
            session.Stop();
        // Job 을 닫으면 Stop 이 놓친 손자 프로세스까지 OS 가 끝낸다.
        _job.Dispose();
    }
}

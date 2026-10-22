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
    public PaneLayout Layout { get; private set; } = new();

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

        // 분할 정보가 없는 옛 파일은 마지막에 보던 세션 하나를 1칸으로 연다.
        Layout = state.Layout ?? new PaneLayout { Slots = [state.ActiveSessionId] };
        Layout.Normalize(SessionOrder);
        if (Layout.Slots.All(s => s is null) && Sessions.Count > 0)
            Layout.Slots[0] = Sessions[0].Id;
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
        Layout.Remove(session.Id);
        Save();
    }

    /// <returns>이전 대화를 이어 갔는지. 이미 떠 있으면 null.</returns>
    public async Task<bool?> StartAsync(Session session, int columns, int rows)
    {
        var resumed = await session.StartAsync(_adapter, _job, columns, rows);
        if (resumed is not null && Sessions.Contains(session))
            Save();
        return resumed;
    }

    public bool HasConversation(Session session) => _adapter.HasConversation(session.Record);

    public void Save() =>
        _store.Save(new AppState
        {
            Sessions = Sessions.Select(s => s.Record).ToList(),
            ActiveSessionId = Layout.FocusedSessionId,
            Layout = Layout,
        });

    public IReadOnlyList<string> SessionOrder => Sessions.Select(s => s.Id).ToList();

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

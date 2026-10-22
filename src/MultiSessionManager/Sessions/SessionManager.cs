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

    /// <summary>창 위치. 창을 닫을 때 채우고, 그 사이의 다른 저장에서도 지워지지 않게 여기 들고 있는다.</summary>
    public WindowBounds? Window { get; set; }

    public SessionManager(SessionStore store, IAgentAdapter adapter)
    {
        _store = store;
        _adapter = adapter;
    }

    public void Load()
    {
        var state = _store.Load();
        Window = state.Window;
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
            Window = Window,
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

    /// <summary>세션이 다 끝난 뒤에도 남은 자손(Claude 의 SessionEnd 훅 등)을 기다려 주는 시간.</summary>
    public static TimeSpan ChildGrace { get; set; } = TimeSpan.FromSeconds(10);

    public void Dispose()
    {
        // 하나씩 닫으면 칸마다 종료 대기가 쌓이고, 뒤쪽 세션은 정리할 시간을 못 받는다. 한꺼번에 닫는다.
        Parallel.ForEach(Sessions.ToList(), session => session.Stop());
        // Claude 는 끝나면서 SessionEnd 훅(대화 기록 올리기 등)을 띄운다. 그것이 끝날 때까지 조금 더 기다린다.
        _job.WaitUntilEmpty(ChildGrace);
        // Job 을 닫으면 그래도 남은 손자 프로세스까지 OS 가 끝낸다.
        _job.Dispose();
    }
}

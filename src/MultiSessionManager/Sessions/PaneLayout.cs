namespace MultiSessionManager.Sessions;

/// <summary>
/// 화면 분할 상태. 칸(slot)마다 세션 하나를 보여 주고, 그중 한 칸이 포커스를 가진다.
/// 저장·복원을 위해 그대로 JSON 으로 직렬화된다.
/// </summary>
public sealed class PaneLayout
{
    public static readonly int[] SupportedModes = [1, 2, 4];

    public int Mode { get; set; } = 1;
    public List<string?> Slots { get; set; } = [null];
    public int Focus { get; set; }

    /// <summary>왼쪽 칸 너비 비율(0.15~0.85).</summary>
    public double ColumnRatio { get; set; } = 0.5;

    /// <summary>위쪽 칸 높이 비율(0.15~0.85). 4분할에서만 쓴다.</summary>
    public double RowRatio { get; set; } = 0.5;

    public string? FocusedSessionId => Focus >= 0 && Focus < Slots.Count ? Slots[Focus] : null;

    public bool IsVisible(string sessionId) => Slots.Contains(sessionId);

    /// <summary>
    /// 칸 수를 바꾼다. 늘릴 때는 아직 안 보이는 세션을 목록 순서대로 채우고,
    /// 줄일 때는 포커스 중인 세션이 남도록 한다.
    /// </summary>
    public void SetMode(int mode, IReadOnlyList<string> sessionOrder)
    {
        if (!SupportedModes.Contains(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Normalize(sessionOrder);
        var focused = FocusedSessionId;

        if (mode < Slots.Count)
        {
            var kept = Slots.Take(mode).ToList();
            var focusIndex = Focus;
            if (focused is not null && !kept.Contains(focused))
            {
                kept[mode - 1] = focused;
                focusIndex = mode - 1;
            }
            Slots = kept;
            Focus = Math.Min(focusIndex, mode - 1);
        }
        else
        {
            var hidden = new Queue<string>(sessionOrder.Where(id => !Slots.Contains(id)));
            while (Slots.Count < mode)
                Slots.Add(hidden.Count > 0 ? hidden.Dequeue() : null);
        }
        Mode = mode;
    }

    /// <summary>세션을 보여 준다. 이미 다른 칸에 있으면 그 칸으로 포커스만 옮긴다.</summary>
    public void Show(string sessionId)
    {
        var index = Slots.IndexOf(sessionId);
        if (index >= 0)
        {
            Focus = index;
            return;
        }
        // 빈 칸이 있으면 포커스 칸을 덮어쓰지 않고 빈 칸부터 채운다.
        var empty = Slots.IndexOf(null);
        if (empty >= 0 && Slots[Focus] is not null)
            Focus = empty;
        Slots[Focus] = sessionId;
    }

    public void FocusSlot(int slot)
    {
        if (slot >= 0 && slot < Slots.Count)
            Focus = slot;
    }

    public void Remove(string sessionId)
    {
        for (var i = 0; i < Slots.Count; i++)
            if (Slots[i] == sessionId)
                Slots[i] = null;
    }

    public void SetRatios(double column, double row)
    {
        ColumnRatio = Clamp(column);
        RowRatio = Clamp(row);
    }

    /// <summary>저장 파일에서 읽은 값이 어긋나 있어도(칸 수·없는 세션·범위 밖 비율) 쓸 수 있게 고친다.</summary>
    public void Normalize(IReadOnlyList<string> sessionOrder)
    {
        if (!SupportedModes.Contains(Mode)) Mode = 1;
        Slots ??= [];
        var known = sessionOrder.ToHashSet();
        var seen = new HashSet<string>();
        for (var i = 0; i < Slots.Count; i++)
        {
            var id = Slots[i];
            if (id is null || !known.Contains(id) || !seen.Add(id))
                Slots[i] = null;
        }
        while (Slots.Count < Mode) Slots.Add(null);
        if (Slots.Count > Mode) Slots = Slots.Take(Mode).ToList();
        if (Focus < 0 || Focus >= Mode) Focus = 0;
        ColumnRatio = Clamp(ColumnRatio);
        RowRatio = Clamp(RowRatio);
    }

    private static double Clamp(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.15, 0.85) : 0.5;
}

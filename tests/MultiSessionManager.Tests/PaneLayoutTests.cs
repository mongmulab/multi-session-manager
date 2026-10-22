using MultiSessionManager.Sessions;

namespace MultiSessionManager.Tests;

public class PaneLayoutTests
{
    private static readonly string[] Order = ["a", "b", "c", "d", "e"];

    [Fact]
    public void Growing_fills_new_slots_with_hidden_sessions_in_list_order()
    {
        var layout = new PaneLayout { Slots = ["c"] };
        layout.SetMode(4, Order);
        Assert.Equal(["c", "a", "b", "d"], layout.Slots);
        Assert.Equal(0, layout.Focus);
    }

    [Fact]
    public void Growing_beyond_session_count_leaves_empty_slots()
    {
        var layout = new PaneLayout { Slots = ["a"] };
        layout.SetMode(4, ["a", "b"]);
        Assert.Equal(["a", "b", null, null], layout.Slots);
    }

    [Fact]
    public void Shrinking_keeps_the_focused_session_visible()
    {
        var layout = new PaneLayout { Mode = 4, Slots = ["a", "b", "c", "d"], Focus = 3 };
        layout.SetMode(1, Order);
        Assert.Equal(["d"], layout.Slots);
        Assert.Equal(0, layout.Focus);

        layout = new PaneLayout { Mode = 4, Slots = ["a", "b", "c", "d"], Focus = 2 };
        layout.SetMode(2, Order);
        Assert.Equal(["a", "c"], layout.Slots);
        Assert.Equal("c", layout.FocusedSessionId);
    }

    [Fact]
    public void Shrinking_keeps_focus_when_it_already_fits()
    {
        var layout = new PaneLayout { Mode = 4, Slots = ["a", "b", "c", "d"], Focus = 1 };
        layout.SetMode(2, Order);
        Assert.Equal(["a", "b"], layout.Slots);
        Assert.Equal(1, layout.Focus);
    }

    [Fact]
    public void Showing_a_visible_session_only_moves_focus()
    {
        var layout = new PaneLayout { Mode = 2, Slots = ["a", "b"], Focus = 0 };
        layout.Show("b");
        Assert.Equal(["a", "b"], layout.Slots);
        Assert.Equal(1, layout.Focus);
    }

    [Fact]
    public void Showing_a_hidden_session_replaces_the_focused_slot()
    {
        var layout = new PaneLayout { Mode = 2, Slots = ["a", "b"], Focus = 1 };
        layout.Show("e");
        Assert.Equal(["a", "e"], layout.Slots);
        Assert.Equal(1, layout.Focus);
    }

    [Fact]
    public void Showing_prefers_an_empty_slot_over_replacing()
    {
        var layout = new PaneLayout { Mode = 4, Slots = ["a", null, "c", null], Focus = 0 };
        layout.Show("e");
        Assert.Equal(["a", "e", "c", null], layout.Slots);
        Assert.Equal(1, layout.Focus);
    }

    [Fact]
    public void Removing_clears_the_slot()
    {
        var layout = new PaneLayout { Mode = 2, Slots = ["a", "b"] };
        layout.Remove("b");
        Assert.Equal(["a", null], layout.Slots);
    }

    [Fact]
    public void Normalize_repairs_a_hand_edited_file()
    {
        var layout = new PaneLayout { Mode = 3, Slots = ["a", "zzz", "a"], Focus = 9, ColumnRatio = 2, RowRatio = double.NaN };
        layout.Normalize(Order);
        Assert.Equal(1, layout.Mode);
        Assert.Equal(["a"], layout.Slots);
        Assert.Equal(0, layout.Focus);
        Assert.Equal(0.85, layout.ColumnRatio);
        Assert.Equal(0.5, layout.RowRatio);
    }

    [Fact]
    public void Normalize_drops_duplicates_and_unknown_sessions()
    {
        var layout = new PaneLayout { Mode = 4, Slots = ["a", "a", "ghost", "b"] };
        layout.Normalize(Order);
        Assert.Equal(["a", null, null, "b"], layout.Slots);
    }

    [Fact]
    public void Unsupported_mode_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaneLayout().SetMode(3, Order));
    }

    [Fact]
    public void Layout_survives_save_and_load()
    {
        var dir = Path.Combine(Path.GetTempPath(), "msm-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SessionStore(Path.Combine(dir, "sessions.json"));
            var a = new SessionRecord { Title = "a" };
            var b = new SessionRecord { Title = "b" };
            var layout = new PaneLayout { Mode = 2, Slots = [a.Id, b.Id], Focus = 1, ColumnRatio = 0.3 };
            store.Save(new AppState { Sessions = [a, b], Layout = layout });

            var loaded = store.Load().Layout!;
            Assert.Equal(2, loaded.Mode);
            Assert.Equal([a.Id, b.Id], loaded.Slots);
            Assert.Equal(1, loaded.Focus);
            Assert.Equal(0.3, loaded.ColumnRatio);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Old_state_file_without_layout_opens_last_active_session()
    {
        var dir = Path.Combine(Path.GetTempPath(), "msm-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "sessions.json");
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, """
                { "Version": 1, "Sessions": [ { "Id": "x", "Title": "x" }, { "Id": "y", "Title": "y" } ], "ActiveSessionId": "y" }
                """);
            using var manager = new SessionManager(new SessionStore(path), new NullAdapterForLayout());
            manager.Load();
            Assert.Equal(1, manager.Layout.Mode);
            Assert.Equal(["y"], manager.Layout.Slots);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private sealed class NullAdapterForLayout : MultiSessionManager.Agents.IAgentAdapter
    {
        public string Kind => "null";
        public MultiSessionManager.Agents.AgentLaunch Prepare(SessionRecord session, int columns, int rows) => throw new NotSupportedException();
        public bool HasConversation(SessionRecord session) => false;
    }
}

using System.Windows;

namespace MultiSessionManager;

/// <summary>앱과 같은 어두운 확인 창. 기본 단추는 "취소" 다(Enter 를 잘못 눌러 지우지 않게).</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string heading, string body, string ok)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeWindow.UseDarkTitleBar(this, 0x00262525);
        Title = title;
        Heading.Text = heading;
        Body.Text = body;
        OkButton.Content = ok;
    }

    private bool _alt;

    public static bool Ask(Window owner, string title, string heading, string body, string ok) =>
        new ConfirmDialog(title, heading, body, ok) { Owner = owner }.ShowDialog() == true;

    /// <summary>두 갈래 중 하나를 고르게 한다. 취소하면 null, 주 단추면 true, 둘째 단추면 false.</summary>
    public static bool? Choose(Window owner, string title, string heading, string body, string ok, string alt)
    {
        var dialog = new ConfirmDialog(title, heading, body, ok) { Owner = owner };
        dialog.AltButton.Content = alt;
        dialog.AltButton.Visibility = Visibility.Visible;
        if (dialog.ShowDialog() != true) return null;
        return !dialog._alt;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnAlt(object sender, RoutedEventArgs e)
    {
        _alt = true;
        DialogResult = true;
    }
}

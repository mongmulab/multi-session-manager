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

    public static bool Ask(Window owner, string title, string heading, string body, string ok) =>
        new ConfirmDialog(title, heading, body, ok) { Owner = owner }.ShowDialog() == true;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}

using System.Windows;

namespace MultiSessionManager;

public partial class RenameDialog : Window
{
    public string Value => NameBox.Text.Trim();

    public RenameDialog(string current)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeWindow.UseDarkTitleBar(this, 0x00262525);
        NameBox.Text = current;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0) return;
        DialogResult = true;
    }
}

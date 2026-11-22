using System.Windows;
using System.Windows.Controls;

namespace SimpleSftp.Desktop;

internal sealed class BackupPasswordWindow : Window
{
    internal string BackupPassword { get; private set; } = "";
    internal BackupPasswordWindow(bool creating)
    {
        Title = creating ? "Encrypt backup" : "Unlock backup";
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Style = (Style)FindResource(typeof(Window));
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = creating ? "Use a password of at least 12 characters. You will need it to restore this backup on any computer." : "Enter the password used when this backup was created.", Margin = new Thickness(0, 12, 0, 20) });
        panel.Children.Add(new TextBlock { Text = "Backup password", Margin = new Thickness(0, 0, 0, 8) });
        var password = new PasswordBox { MaxLength = 1024 }; panel.Children.Add(password);
        var confirmation = new PasswordBox { MaxLength = 1024 };
        if (creating)
        {
            panel.Children.Add(new TextBlock { Text = "Confirm password", Margin = new Thickness(0, 16, 0, 8) });
            panel.Children.Add(confirmation);
        }
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        var accept = new Button { Content = creating ? "Create backup" : "Restore backup", IsDefault = true, Style = (Style)FindResource("Primary") };
        accept.Click += (_, _) =>
        {
            if (password.Password.Length < 12) { error.Text = "Use at least 12 characters."; return; }
            if (creating && password.Password != confirmation.Password) { error.Text = "Passwords do not match."; return; }
            BackupPassword = password.Password; DialogResult = true;
        };
        buttons.Children.Add(accept); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => password.Focus();
        Closed += (_, _) => { password.Clear(); confirmation.Clear(); };
    }
}

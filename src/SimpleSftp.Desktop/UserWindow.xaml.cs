using System.Windows;
using System.IO;
using Microsoft.Win32;
using SimpleSftp.Core;

namespace SimpleSftp.Desktop;
public partial class UserWindow : Window
{
    private readonly SftpUser? original;
    public SftpUser? Result { get; private set; }
    public UserWindow(SftpUser? user)
    {
        InitializeComponent();
        original = user;
        if (user != null)
        {
            Title = "Edit user"; Heading.Text = "Edit SFTP user";
            PasswordAuthenticationInput.IsChecked = user.PasswordAuthenticationEnabled;
            AuthorizedKeysInput.Text = string.Join(Environment.NewLine, user.AuthorizedKeys);
            UsernameInput.Text = user.Username; FolderInput.Text = user.HomeDirectory;
            ReadOnlyInput.IsChecked = user.ReadOnly; EnabledInput.IsChecked = user.Enabled;
            PasswordLabel.Text = "New password · leave blank to keep current";
        }
        Loaded += (_, _) => UsernameInput.Focus();
    }
    private void PasswordAuthentication_Changed(object sender, RoutedEventArgs e)
    {
        if (PasswordInput != null) PasswordInput.IsEnabled = PasswordAuthenticationInput.IsChecked == true;
    }
    private void ImportPublicKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Public SSH keys (*.pub)|*.pub|All files (*.*)|*.*", Title = "Import an SSH public key" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 16384) throw new ArgumentException("The public-key file is too large.");
            var line = File.ReadAllText(dialog.FileName).Trim(); PublicKeys.Parse(line);
            AuthorizedKeysInput.Text = string.IsNullOrWhiteSpace(AuthorizedKeysInput.Text) ? line : AuthorizedKeysInput.Text.TrimEnd() + Environment.NewLine + line;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose this user's home folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true) FolderInput.Text = dialog.FolderName;
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            var password = PasswordInput.Password;
            var result = new SftpUser
            {
                Username = UsernameInput.Text.Trim(), HomeDirectory = FolderInput.Text.Trim(),
                ReadOnly = ReadOnlyInput.IsChecked == true, Enabled = EnabledInput.IsChecked == true,
                PasswordAuthenticationEnabled = PasswordAuthenticationInput.IsChecked == true,
                AuthorizedKeys = AuthorizedKeysInput.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).ToList(),
                PasswordHash = string.IsNullOrEmpty(password) ? original?.PasswordHash ?? "" : await Task.Run(() => Passwords.Hash(password))
            };
            result.Validate(); Result = result; DialogResult = true;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
        finally { SaveButton.IsEnabled = true; }
    }
}


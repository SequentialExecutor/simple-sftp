using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SimpleSftp.Core;

namespace SimpleSftp.Desktop;
public partial class MainWindow : Window
{
    private readonly SettingsStore store;
    private readonly SftpHost host;
    private ServerSettings settings;
    private readonly ObservableCollection<SftpUser> users = [];
    private readonly ObservableCollection<ActivityEntry> activity = [];
    private bool busy;
    private bool closing;
    private bool loadingPreferences = true;
    private bool loadingAddress = true;
    private TrayIcon? trayIcon;
    private WindowState restoredState = WindowState.Normal;
    internal bool IsTrayIconVisible => trayIcon?.Visible == true;
    internal TrayIcon? TrayMenu => trayIcon;

    public MainWindow(string dataDirectory) : this(dataDirectory, new WindowsStartup()) { }

    internal MainWindow(string dataDirectory, IStartupRegistration startupRegistration)
    {
        this.startupRegistration = startupRegistration;
        InitializeComponent();
        store = new(dataDirectory);
        settings = store.Load();
        host = new(dataDirectory);
        foreach (var user in settings.Users) users.Add(user);
        UsersGrid.ItemsSource = users;
        ActivityGrid.ItemsSource = activity;
        LoadListenAddress();
        PortInput.Text = settings.Port.ToString();
        LimitInput.Text = settings.MaxConnections.ToString();
        LoadPreferenceControls();
        loadingPreferences = false;
        host.Activity += entry => Dispatcher.BeginInvoke(() =>
        {
            activity.Insert(0, entry);
            if (activity.Count > 500) activity.RemoveAt(500);
        });
        host.ConnectionsChanged += () => Dispatcher.BeginInvoke(Refresh);
        trayIcon = new TrayIcon(RestoreFromTray, ExitFromTray, ToggleServerAsync);
        InitializeLiveView();
        Loaded += MainWindow_Loaded;
        Refresh();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (trayIcon == null || closing) return;
        if (WindowState == WindowState.Minimized)
        {
            if (settings.MinimizeToTray)
            {
                trayIcon.Visible = true;
                Hide();
            }
        }
        else restoredState = WindowState;
    }

    internal void RestoreFromTray()
    {
        if (closing) return;
        Show();
        WindowState = restoredState;
        Activate();
        trayIcon!.Visible = false;
    }

    internal void ExitFromTray() => Close();

    protected override void OnClosed(EventArgs e)
    {
        monitorTimer.Stop();
        trayIcon?.Dispose();
        trayIcon = null;
        base.OnClosed(e);
    }

    private void Refresh()
    {
        var running = host.IsRunning;
        trayIcon?.Update(running, host.ConnectionCount, busy);
        StatusBadge.Text = running ? "\u25CF  Running" : "\u25CF  Stopped";
        StatusBadge.Foreground = new System.Windows.Media.SolidColorBrush(running
            ? System.Windows.Media.Color.FromRgb(36, 99, 68) : System.Windows.Media.Color.FromRgb(82, 94, 112));
        StatusContainer.Background = new System.Windows.Media.SolidColorBrush(running
            ? System.Windows.Media.Color.FromRgb(233, 244, 238) : System.Windows.Media.Color.FromRgb(233, 236, 241));
        HeroTitle.Text = running ? "Server is running" : "Ready to share";
        HeroDescription.Text = running
            ? (settings.MinimizeToTray ? "Sharing securely. Minimize to manage your server from the tray." : "Sharing securely. Your server keeps running when minimized.")
            : "Add a user, choose their folder, and start your server.";
        StartButton.Content = running ? "Stop server" : "Start server";
        StartButton.IsEnabled = !busy;
        ConnectionForm.IsEnabled = !running && !busy;
        MinimizeToTrayInput.IsEnabled = !busy;
        RefreshFeatureControls();
        AddButton.IsEnabled = !running && !busy;
        RecheckFoldersButton.IsEnabled = !busy && !checkingHomeFolders;
        EditButton.IsEnabled = RemoveButton.IsEnabled = !running && !busy && UsersGrid.SelectedItem != null;
        EmptyUsers.Visibility = users.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EndpointText.Text = $"{(settings.ListenAddress.Contains(':') ? $"[{settings.ListenAddress}]" : settings.ListenAddress)}:{settings.Port}";
        UserCountText.Text = $"{users.Count(u => u.Enabled)} enabled";
        ConnectionsText.Text = host.ConnectionCount.ToString();
        FingerprintText.Text = host.Fingerprint;
        EditHint.Text = running ? "Stop the server to change users." : "Changes are saved automatically.";
        var address = settings.ListenAddress is "0.0.0.0" or "::" ? "<server-ip>" : settings.ListenAddress;
        CommandText.Text = $"sftp -P {settings.Port} {(users.FirstOrDefault()?.Username ?? "username")}@{(address.Contains(':') ? $"[{address}]" : address)}";
    }

    private void LoadListenAddress()
    {
        loadingAddress = true;
        var automatic = NetworkAddresses.IsAutomatic(settings);
        settings.AutoDetectListenAddress = automatic;
        if (automatic) settings.ListenAddress = NetworkAddresses.Detect();
        UseCurrentAddressInput.IsChecked = automatic;
        AddressInput.Text = settings.ListenAddress;
        AddressInput.IsReadOnly = automatic;
        loadingAddress = false;
    }

    private void UseCurrentAddress_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingAddress) return;
        var automatic = UseCurrentAddressInput.IsChecked == true;
        AddressInput.IsReadOnly = automatic;
        if (automatic) AddressInput.Text = NetworkAddresses.Detect();
    }

    private ServerSettings ReadForm()
    {
        if (!int.TryParse(PortInput.Text, out var port)) throw new ArgumentException("Enter a whole number for the port.");
        if (!int.TryParse(LimitInput.Text, out var limit)) throw new ArgumentException("Enter a whole number for the connection limit.");
        var result = new ServerSettings { ListenAddress = UseCurrentAddressInput.IsChecked == true ? NetworkAddresses.Detect() : AddressInput.Text.Trim(), AutoDetectListenAddress = UseCurrentAddressInput.IsChecked == true, Port = port, MaxConnections = limit, MinimizeToTray = settings.MinimizeToTray, StartWithWindows = settings.StartWithWindows, StartMinimized = settings.StartMinimized, AutoStartServer = settings.AutoStartServer, Users = users.ToList() };
        result.Validate(false);
        return result;
    }
    private void Save()
    {
        var candidate = ReadForm();
        // Store each repaired account even if other restored accounts still
        // need attention. Starting the server performs the full folder check.
        store.Save(candidate, false);
        settings = candidate;
        LoadListenAddress();
        FooterText.Text = "Settings saved";
        Refresh();
    }
    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Save(); await CheckHomeFoldersAsync(); } catch (Exception ex) { ShowError(ex); }
    }
    private async void ToggleServer_Click(object sender, RoutedEventArgs e) => await ToggleServerAsync();

    private async Task ToggleServerAsync()
    {
        if (busy || closing) return;
        busy = true; Refresh();
        try
        {
            if (host.IsRunning) await host.StopAsync();
            else
            {
                if (!await CheckHomeFoldersAsync()) { ShowHomeFolderError(); return; }
                Save(); await host.StartAsync(settings);
            }
            FooterText.Text = host.IsRunning ? "Server running · " + DateTime.Now.ToString("t") : "Server stopped";
        }
        catch (Exception ex)
        {
            if (!IsVisible) RestoreFromTray();
            ShowError(ex);
        }
        finally { busy = false; Refresh(); }
    }
    private async void AddUser_Click(object sender, RoutedEventArgs e) => await EditUserAsync(null);
    private async void EditUser_Click(object sender, RoutedEventArgs e) => await EditUserAsync(UsersGrid.SelectedItem as SftpUser);
    private async void UsersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!host.IsRunning && !busy && UsersGrid.SelectedItem is SftpUser user) await EditUserAsync(user);
    }
    private async Task EditUserAsync(SftpUser? original)
    {
        if (host.IsRunning || busy) return;
        var dialog = new UserWindow(original) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var index = original == null ? users.Count : users.IndexOf(original);
        if (original == null) users.Add(dialog.Result!); else users[index] = dialog.Result!;
        try { Save(); }
        catch (Exception ex)
        {
            if (original == null) users.RemoveAt(index); else users[index] = original;
            ShowError(ex);
        }
        await CheckHomeFoldersAsync();
        Refresh();
    }
    private async void RemoveUser_Click(object sender, RoutedEventArgs e)
    {
        if (host.IsRunning || busy || UsersGrid.SelectedItem is not SftpUser user) return;
        if (MessageBox.Show(this, $"Remove the SFTP account '{user.Username}'? Files in their folder will stay on disk.", "Remove user", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var index = users.IndexOf(user);
        users.Remove(user);
        try { Save(); } catch (Exception ex) { users.Insert(index, user); ShowError(ex); }
        await CheckHomeFoldersAsync();
        Refresh();
    }
    private void UsersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (host != null) Refresh(); }
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", store.DataDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        if (!busy && !host.IsRunning) { closing = true; return; }
        e.Cancel = true;
        if (busy) return;
        busy = true; Refresh();
        try { await host.StopAsync(); closing = true; _ = Dispatcher.BeginInvoke(Close); }
        catch (Exception ex) { busy = false; Refresh(); ShowError(ex); }
    }
    private void ShowError(Exception ex) => MessageBox.Show(this, ex.Message, "Simple SFTP", MessageBoxButton.OK, MessageBoxImage.Warning);
}





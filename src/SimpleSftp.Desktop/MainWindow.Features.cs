using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SimpleSftp.Core;

namespace SimpleSftp.Desktop;

public partial class MainWindow
{
    private readonly IStartupRegistration startupRegistration;
    private readonly ObservableCollection<ClientSession> liveSessions = [];
    private readonly ObservableCollection<FileTransfer> liveTransfers = [];
    private readonly DispatcherTimer monitorTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private void InitializeLiveView()
    {
        SessionsGrid.ItemsSource = liveSessions;
        TransfersGrid.ItemsSource = liveTransfers;
        monitorTimer.Tick += (_, _) => RefreshLiveView();
        monitorTimer.Start();
    }

    private void RefreshFeatureControls()
    {
        StartWithWindowsInput.IsEnabled = !busy;
        StartMinimizedInput.IsEnabled = !busy && settings.StartWithWindows;
        AutoStartServerInput.IsEnabled = !busy;
        ExportBackupButton.IsEnabled = !busy;
        RestoreBackupButton.IsEnabled = !busy && !host.IsRunning;
        DisconnectButton.IsEnabled = !busy && SessionsGrid.SelectedItem is ClientSession;
    }

    internal void RefreshLiveView()
    {
        if (closing) return;
        var snapshot = host.Sessions;
        if (!liveSessions.SequenceEqual(snapshot))
        {
            var selected = (SessionsGrid.SelectedItem as ClientSession)?.Id;
            liveSessions.Clear(); foreach (var item in snapshot) liveSessions.Add(item);
            SessionsGrid.SelectedItem = liveSessions.FirstOrDefault(s => s.Id == selected);
        }
        var transfers = host.Transfers;
        if (!liveTransfers.SequenceEqual(transfers))
        {
            var selected = (TransfersGrid.SelectedItem as FileTransfer)?.Id;
            // Update rows in place so progress refreshes do not reset scrolling.
            for (var i = 0; i < transfers.Length; i++)
            {
                if (i >= liveTransfers.Count) liveTransfers.Add(transfers[i]);
                else if (liveTransfers[i].Id == transfers[i].Id)
                {
                    if (liveTransfers[i] != transfers[i]) liveTransfers[i] = transfers[i];
                }
                else
                {
                    var existing = liveTransfers.Select((item, index) => (item.Id, index)).FirstOrDefault(p => p.Item1 == transfers[i].Id, (Guid.Empty, -1));
                    if (existing.Item2 >= 0) { liveTransfers.Move(existing.Item2, i); liveTransfers[i] = transfers[i]; }
                    else liveTransfers.Insert(i, transfers[i]);
                }
            }
            while (liveTransfers.Count > transfers.Length) liveTransfers.RemoveAt(liveTransfers.Count - 1);
            TransfersGrid.SelectedItem = liveTransfers.FirstOrDefault(t => t.Id == selected);
        }
        RefreshFeatureControls();
    }

    private void SessionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (host != null) RefreshFeatureControls(); }

    private async void DisconnectSession_Click(object sender, RoutedEventArgs e)
    {
        if (busy || SessionsGrid.SelectedItem is not ClientSession selected) return;
        busy = true; Refresh();
        try { await host.DisconnectSessionAsync(selected.Id); FooterText.Text = "Session disconnected"; }
        catch (Exception ex) { ShowError(ex); }
        finally { busy = false; RefreshLiveView(); Refresh(); }
    }

    private void LoadPreferenceControls()
    {
        MinimizeToTrayInput.IsChecked = settings.MinimizeToTray;
        StartWithWindowsInput.IsChecked = settings.StartWithWindows;
        StartMinimizedInput.IsChecked = settings.StartMinimized;
        AutoStartServerInput.IsChecked = settings.AutoStartServer;
    }

    private void Preference_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingPreferences) return;
        var candidate = JsonSerializer.Deserialize<ServerSettings>(JsonSerializer.Serialize(settings))!;
        candidate.MinimizeToTray = MinimizeToTrayInput.IsChecked == true;
        candidate.StartWithWindows = StartWithWindowsInput.IsChecked == true;
        candidate.StartMinimized = StartMinimizedInput.IsChecked == true;
        candidate.AutoStartServer = AutoStartServerInput.IsChecked == true;
        var registrationChanged = candidate.StartWithWindows != settings.StartWithWindows;
        string? previousCommand = null;
        var registrationWritten = false;
        try
        {
            if (registrationChanged)
            {
                previousCommand = startupRegistration.Read();
                var executable = Path.Combine(Path.GetDirectoryName(typeof(App).Assembly.Location)!, "SimpleSftp.Desktop.exe");
                startupRegistration.Write(candidate.StartWithWindows ? WindowsStartup.BuildCommand(executable, store.DataDirectory) : null);
                registrationWritten = true;
            }
            store.Save(candidate, false);
            settings = candidate;
            FooterText.Text = "Application settings saved";
            Refresh();
        }
        catch (Exception ex)
        {
            if (registrationWritten)
            {
                try { startupRegistration.Write(previousCommand); }
                catch (Exception rollback) { ex = new IOException(ex.Message + " Startup registration could not be restored: " + rollback.Message, ex); }
            }
            loadingPreferences = true; LoadPreferenceControls(); loadingPreferences = false;
            ShowError(ex);
        }
    }

    internal async Task ApplyLaunchOptionsAsync(bool windowsStartup)
    {
        var foldersReady = await CheckHomeFoldersAsync();
        if (settings.AutoStartServer && foldersReady) await ToggleServerAsync();
        else if (settings.AutoStartServer && !foldersReady) ShowHomeFolderError();
        if (windowsStartup && settings.StartMinimized && !users.Any(user => user.IsHomeDirectoryMissing) && (!settings.AutoStartServer || host.IsRunning))
            WindowState = WindowState.Minimized;
    }

    private async void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var destination = new SaveFileDialog { Title = "Create encrypted backup", Filter = "Simple SFTP backup (*.sftpbak)|*.sftpbak", FileName = "SimpleSftp-" + DateTime.Now.ToString("yyyyMMdd") + ".sftpbak" };
        if (destination.ShowDialog(this) != true) return;
        var password = new BackupPasswordWindow(true) { Owner = this };
        if (password.ShowDialog() != true) return;
        busy = true; Refresh();
        try { await Task.Run(() => BackupService.Export(store, destination.FileName, password.BackupPassword)); FooterText.Text = "Encrypted backup created"; }
        catch (Exception ex) { ShowError(ex); }
        finally { busy = false; Refresh(); }
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (busy || host.IsRunning) return;
        var source = new OpenFileDialog { Title = "Restore encrypted backup", Filter = "Simple SFTP backup (*.sftpbak)|*.sftpbak" };
        if (source.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, "Replace current accounts, preferences, and the host identity with this backup? Shared files will stay on disk. Windows startup and automatic server start will be turned off.", "Restore backup", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var password = new BackupPasswordWindow(false) { Owner = this };
        if (password.ShowDialog() != true) return;
        busy = true; Refresh();
        try { await RestoreBackupAsync(source.FileName, password.BackupPassword); FooterText.Text = "Backup restored. Review user folders before starting the server."; }
        catch (Exception ex) { ShowError(ex); }
        finally { busy = false; Refresh(); }
    }

    internal async Task RestoreBackupAsync(string source, string password)
    {
        if (host.IsRunning) throw new InvalidOperationException("Stop the server before restoring a backup.");
        var previousCommand = settings.StartWithWindows ? startupRegistration.Read() : null;
        if (settings.StartWithWindows) startupRegistration.Write(null);
        ServerSettings restored;
        try { restored = await Task.Run(() => BackupService.Restore(store, source, password)); }
        catch { if (previousCommand != null) startupRegistration.Write(previousCommand); throw; }
        settings = restored;
        users.Clear(); foreach (var user in settings.Users) users.Add(user);
        LoadListenAddress(); PortInput.Text = settings.Port.ToString(); LimitInput.Text = settings.MaxConnections.ToString();
        loadingPreferences = true; LoadPreferenceControls(); loadingPreferences = false;
        host.ReloadIdentity();
        await CheckHomeFoldersAsync();
        Refresh();
    }
}

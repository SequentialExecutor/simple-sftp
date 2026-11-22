using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SimpleSftp.Core;
using SimpleSftp.Desktop;

internal static class UiSmoke
{

    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SimpleSftp.Desktop;component/Theme.xaml", UriKind.Relative) });
            _ = app.Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var data = Path.GetFullPath(Path.Combine("artifacts", "ui-smoke", Guid.NewGuid().ToString("N")));
                    Directory.CreateDirectory(data);
                    // Older settings files have no minimize preference.
                    File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
                    var window = new MainWindow(data);
                    window.Show();
                    await Task.Delay(150);
                    if (((TextBox)window.FindName("AddressInput")).Text != NetworkAddresses.Detect() || ((CheckBox)window.FindName("UseCurrentAddressInput")).IsChecked != true)
                        throw new Exception("The computer's current address was not populated automatically.");
                    ((CheckBox)window.FindName("UseCurrentAddressInput")).IsChecked = false;
                    var addressInput = (TextBox)window.FindName("AddressInput");
                    if (addressInput.IsReadOnly) throw new Exception("Manual address override is not editable.");
                    addressInput.Text = "127.0.0.1";
                    ((CheckBox)window.FindName("UseCurrentAddressInput")).IsChecked = true;
                    if (addressInput.Text != NetworkAddresses.Detect()) throw new Exception("Re-enabling detection did not refresh the address.");
                    Console.WriteLine("PASS: Listen address detects the computer's current IP and supports a manual override.");
                    Capture(window, "artifacts/preview.png");
                    var dialog = new UserWindow(null) { Owner = window };
                    dialog.Show();
                    await Task.Delay(100);
                    Capture(dialog, "artifacts/user-dialog.png");
                    AssertInside(dialog, (FrameworkElement)dialog.FindName("SaveButton"));
                    dialog.Height = dialog.MinHeight;
                    dialog.Width = dialog.MinWidth;
                    dialog.UpdateLayout();
                    AssertInside(dialog, (FrameworkElement)dialog.FindName("SaveButton"));
                    Console.WriteLine("PASS: User dialog keeps its save action visible at its minimum size.");
                    dialog.Close();
                    window.WindowState = WindowState.Minimized;
                    await Task.Delay(100);
                    if (window.IsVisible || !window.IsTrayIconVisible) throw new Exception("Stopped window did not minimize to the tray.");
                    window.RestoreFromTray();
                    if (!window.IsVisible || window.IsTrayIconVisible) throw new Exception("Stopped window did not restore from the tray.");
                    Console.WriteLine("PASS: Stopped app minimizes to the tray and restores, including with older settings files.");
                    ((CheckBox)window.FindName("MinimizeToTrayInput")).IsChecked = false;
                    if (new SettingsStore(data).Load().MinimizeToTray) throw new Exception("Disabling tray minimization was not saved.");
                    window.WindowState = WindowState.Minimized;
                    await Task.Delay(50);
                    if (!window.IsVisible || window.IsTrayIconVisible) throw new Exception("Disabled tray setting did not leave the minimized window in the taskbar.");
                    window.WindowState = WindowState.Normal;
                    Console.WriteLine("PASS: Disabling tray minimization saves immediately and minimizes to the taskbar.");
                    window.Close();
                    if (window.IsTrayIconVisible) throw new Exception("Closing the stopped window left a tray icon.");
                    if (window.IsVisible) throw new Exception("Stopped window did not close.");
                    Console.WriteLine("PASS: Main window and user dialog render and close.");
                    var reopened = new MainWindow(data);
                    reopened.Show();
                    if (((CheckBox)reopened.FindName("MinimizeToTrayInput")).IsChecked != false)
                        throw new Exception("Minimize preference was not restored after reopening.");
                    reopened.WindowState = WindowState.Minimized;
                    if (!reopened.IsVisible || reopened.IsTrayIconVisible) throw new Exception("Reopened app ignored the persisted taskbar preference.");
                    reopened.Close();
                    Console.WriteLine("PASS: Minimize preference survives closing and reopening the application.");
                    var home = Path.Combine(data, "shared");
                    Directory.CreateDirectory(home);
                    var settings = new ServerSettings { AutoDetectListenAddress = false, Port = 0, Users = [new SftpUser { Username = "smoke-test", HomeDirectory = home, PasswordHash = Passwords.Hash("ui-test-password-942!") }] };
                    var portProbe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                    portProbe.Start(); settings.Port = ((System.Net.IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
                    new SettingsStore(data).Save(settings);
                    var runningWindow = new MainWindow(data);
                    runningWindow.Show();
                    ((CheckBox)runningWindow.FindName("MinimizeToTrayInput")).IsChecked = false;
                    ((Button)runningWindow.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(150);
                    if (!((TextBlock)runningWindow.FindName("StatusBadge")).Text.Contains("Running")) throw new Exception("Start button did not start the server.");
                    Capture(runningWindow, "artifacts/running-preview.png");
                    var navigation = (ListBox)runningWindow.FindName("Navigation");
                    var pages = (TabControl)runningWindow.FindName("MainTabs");
                    Directory.CreateDirectory("artifacts/ui-refresh");
                    var pageNames = new[] { "users", "sessions", "connection", "settings", "activity", "quick-start" };
                    for (var page = 0; page < pageNames.Length; page++)
                    {
                        navigation.SelectedIndex = page;
                        runningWindow.UpdateLayout();
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        if (pages.SelectedIndex != page || pages.SelectedContent == null) throw new Exception("Sidebar navigation did not select " + pageNames[page]);
                        Capture(runningWindow, "artifacts/ui-refresh/" + pageNames[page] + ".png");
                    }
                    Console.WriteLine("PASS: Sidebar navigation opens all six pages and stays synchronized with page selection.");
                    runningWindow.Width = runningWindow.MinWidth;
                    runningWindow.Height = runningWindow.MinHeight;
                    foreach (var page in Enumerable.Range(0, pageNames.Length))
                    {
                        navigation.SelectedIndex = page;
                        runningWindow.UpdateLayout();
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        AssertInside(runningWindow, (FrameworkElement)runningWindow.FindName("StartButton"));
                        AssertInside(runningWindow, navigation);
                        AssertInside(runningWindow, (FrameworkElement)runningWindow.FindName("FooterText"));
                        Capture(runningWindow, "artifacts/ui-refresh/" + pageNames[page] + "-compact.png");
                    }
                    Console.WriteLine("PASS: Server action, navigation, and footer remain visible on every page at minimum window size.");
                    runningWindow.Width = 1180;
                    runningWindow.Height = 800;
                    navigation.SelectedIndex = 0;
                    if (new SettingsStore(data).Load().MinimizeToTray) throw new Exception("Starting the server reset the saved minimize preference.");
                    if (((TextBox)runningWindow.FindName("AddressInput")).Text != "127.0.0.1" || ((CheckBox)runningWindow.FindName("UseCurrentAddressInput")).IsChecked != false)
                        throw new Exception("A saved manual listen address was overwritten by automatic detection.");
                    Console.WriteLine("PASS: Saved manual listen addresses remain unchanged when the app reopens and starts.");
                    runningWindow.WindowState = WindowState.Minimized;
                    if (!runningWindow.IsVisible || runningWindow.IsTrayIconVisible) throw new Exception("Running app ignored the taskbar preference.");
                    runningWindow.WindowState = WindowState.Normal;
                    var minimizeInput = (CheckBox)runningWindow.FindName("MinimizeToTrayInput");
                    if (!minimizeInput.IsEnabled) throw new Exception("Minimize preference cannot be changed while the server is running.");
                    minimizeInput.IsChecked = true;
                    if (!new SettingsStore(data).Load().MinimizeToTray) throw new Exception("Changing the minimize preference while running was not saved.");
                    Console.WriteLine("PASS: Minimize preference can change while running and survives saving connection settings.");
                    ((TabItem)runningWindow.FindName("SettingsTab")).IsSelected = true;
                    Capture(runningWindow, "artifacts/settings-preview.png");
                    runningWindow.WindowState = WindowState.Maximized;
                    runningWindow.WindowState = WindowState.Minimized;
                    await Task.Delay(100);
                    if (runningWindow.IsVisible || !runningWindow.IsTrayIconVisible) throw new Exception("Running window did not minimize to the tray.");
                    using (var client = new System.Net.Sockets.TcpClient())
                    {
                        await client.ConnectAsync(System.Net.IPAddress.Loopback, settings.Port);
                        var banner = new byte[128];
                        var received = await client.GetStream().ReadAsync(banner).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                        if (!System.Text.Encoding.ASCII.GetString(banner, 0, received).StartsWith("SSH-2.0-SimpleSftp"))
                            throw new Exception("Minimized app did not serve an SSH connection.");
                    }
                    Console.WriteLine("PASS: Minimized app keeps its SSH listener running in the background.");
                    runningWindow.RestoreFromTray();
                    if (!runningWindow.IsVisible || runningWindow.IsTrayIconVisible || runningWindow.WindowState != WindowState.Maximized)
                        throw new Exception("Restoring did not preserve the maximized window state.");
                    Console.WriteLine("PASS: Tray restore brings back the maximized window and removes its tray icon.");
                    var closed = false;
                    runningWindow.Closed += (_, _) => closed = true;
                    runningWindow.Close();
                    await Until(() => closed);
                    if (runningWindow.IsTrayIconVisible) throw new Exception("Closing the running window left a tray icon.");
                    Console.WriteLine("PASS: Start button starts listener; closing a running window stops it and exits.");

                    var trayWindow = new MainWindow(data);
                    trayWindow.Show();
                    trayWindow.WindowState = WindowState.Minimized;
                    if (trayWindow.TrayMenu!.ServerAction.Text != "Start server" || !trayWindow.TrayMenu.ServerAction.Enabled)
                        throw new Exception("Stopped tray menu did not offer Start server.");
                    trayWindow.TrayMenu.ServerAction.PerformClick();
                    await Until(() => ((TextBlock)trayWindow.FindName("StatusBadge")).Text.Contains("Running"));
                    if (trayWindow.IsVisible || !trayWindow.IsTrayIconVisible || trayWindow.TrayMenu.ServerAction.Text != "Stop server")
                        throw new Exception("Starting from the tray restored the window or left the wrong menu action.");
                    using var trayClient = new System.Net.Sockets.TcpClient();
                    await trayClient.ConnectAsync(System.Net.IPAddress.Loopback, settings.Port);
                    var trayBanner = new byte[128];
                    var trayReceived = await trayClient.GetStream().ReadAsync(trayBanner).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    if (!System.Text.Encoding.ASCII.GetString(trayBanner, 0, trayReceived).StartsWith("SSH-2.0-SimpleSftp"))
                        throw new Exception("Server started from the tray did not accept an SSH connection.");
                    Console.WriteLine("PASS: Tray Start server starts the listener and accepts SSH connections while the window stays hidden.");
                    trayWindow.TrayMenu.ServerAction.PerformClick();
                    if (trayWindow.TrayMenu.ServerAction.Enabled) throw new Exception("Tray server action remained enabled during shutdown.");
                    // A repeated click during shutdown must not restart the listener.
                    trayWindow.TrayMenu.ServerAction.PerformClick();
                    await Until(() => trayWindow.TrayMenu.ServerAction.Enabled && trayWindow.TrayMenu.ServerAction.Text == "Start server");
                    if (trayWindow.IsVisible || !trayWindow.IsTrayIconVisible)
                        throw new Exception("Stopping from the tray did not keep the app in the tray.");
                    using (var afterStop = new System.Net.Sockets.TcpClient())
                    {
                        try
                        {
                            await afterStop.ConnectAsync(System.Net.IPAddress.Loopback, settings.Port);
                            throw new Exception("Tray Stop server left the listener open.");
                        }
                        catch (System.Net.Sockets.SocketException) { }
                    }
                    Console.WriteLine("PASS: Tray Stop server stops an active listener, prevents duplicate actions, and keeps the app hidden.");
                    trayWindow.TrayMenu.ServerAction.PerformClick();
                    await Until(() => trayWindow.TrayMenu.ServerAction.Enabled && trayWindow.TrayMenu.ServerAction.Text == "Stop server");
                    if (trayWindow.IsVisible) throw new Exception("Tray restart restored the window unexpectedly.");
                    Console.WriteLine("PASS: Tray server action updates correctly and can restart the stopped server.");
                    var trayClosed = false;
                    trayWindow.Closed += (_, _) => trayClosed = true;
                    trayWindow.ExitFromTray();
                    await Until(() => trayClosed);
                    if (trayWindow.IsTrayIconVisible) throw new Exception("Exiting from the tray left its icon visible.");
                    using var stoppedClient = new System.Net.Sockets.TcpClient();
                    try
                    {
                        await stoppedClient.ConnectAsync(System.Net.IPAddress.Loopback, settings.Port);
                        throw new Exception("The listener remained open after exiting from the tray.");
                    }
                    catch (System.Net.Sockets.SocketException) { }
                    Console.WriteLine("PASS: Exit from the tray closes the hidden window, removes its icon, and stops the listener.");

                    var registration = new MemoryStartup();
                    var preferences = new MainWindow(data, registration);
                    preferences.Show();
                    ((CheckBox)preferences.FindName("StartWithWindowsInput")).IsChecked = true;
                    if (registration.Command == null || !registration.Command.Contains("--startup --data-dir")) throw new Exception("Enabling Windows startup did not register the app.");
                    ((CheckBox)preferences.FindName("StartWithWindowsInput")).IsChecked = false;
                    if (registration.Command != null || new SettingsStore(data).Load().StartWithWindows) throw new Exception("Disabling Windows startup did not remove registration.");
                    ((CheckBox)preferences.FindName("StartWithWindowsInput")).IsChecked = true;
                    ((CheckBox)preferences.FindName("AutoStartServerInput")).IsChecked = true;
                    preferences.Close();
                    Console.WriteLine("PASS: Startup preferences save, register, and unregister through an isolated registration provider.");
                    var automatic = new MainWindow(data, registration);
                    automatic.Show();
                    if (((CheckBox)automatic.FindName("AutoStartServerInput")).IsChecked != true) throw new Exception("Automatic startup preference was not restored.");
                    await automatic.ApplyLaunchOptionsAsync(true);
                    if (!automatic.IsTrayIconVisible || automatic.IsVisible || !((TextBlock)automatic.FindName("StatusBadge")).Text.Contains("Running"))
                        throw new Exception("Windows startup did not start the server minimized.");
                    Console.WriteLine("PASS: Windows startup launches minimized and automatically starts the server when enabled.");

                    using (var live = new System.Net.Sockets.TcpClient())
                    {
                        await live.ConnectAsync(System.Net.IPAddress.Loopback, settings.Port);
                        await Until(() => ((TextBlock)automatic.FindName("ConnectionsText")).Text != "0");
                        automatic.RefreshLiveView();
                        var sessions = (DataGrid)automatic.FindName("SessionsGrid");
                        if (sessions.Items.Count != 1) throw new Exception("Live session was not rendered.");
                        automatic.RestoreFromTray();
                        ((TabItem)automatic.FindName("SessionsTab")).IsSelected = true;
                        automatic.UpdateLayout();
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        if (sessions.ItemContainerGenerator.ContainerFromIndex(0) is not DataGridRow visibleRow || visibleRow.ActualHeight <= 0)
                            throw new Exception("Live session row was not visible after navigation.");
                        Capture(automatic, "artifacts/ui-refresh/sessions-live.png");
                        sessions.SelectedIndex = 0;
                        ((Button)automatic.FindName("DisconnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Until(() => ((TextBlock)automatic.FindName("ConnectionsText")).Text == "0");
                        automatic.RefreshLiveView();
                        if (sessions.Items.Count != 0) throw new Exception("Disconnect did not remove the displayed live session.");
                    }
                    Console.WriteLine("PASS: Live Sessions displays connected clients and its Disconnect control removes the selected session.");

                    var backupPath = Path.Combine(Path.GetDirectoryName(data)!, "ui-backup-" + Guid.NewGuid().ToString("N") + ".sftpbak");
                    const string backupPassword = "ui-backup-password-942!";
                    await Task.Run(() => BackupService.Export(new SettingsStore(data), backupPath, backupPassword));
                    var blockedRestore = false;
                    try { await automatic.RestoreBackupAsync(backupPath, backupPassword); } catch (InvalidOperationException) { blockedRestore = true; }
                    if (!blockedRestore) throw new Exception("Restore was allowed while the server was running.");
                    ((Button)automatic.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => ((Button)automatic.FindName("StartButton")).IsEnabled && ((TextBlock)automatic.FindName("StatusBadge")).Text.Contains("Stopped"));
                    automatic.RestoreFromTray();
                    await automatic.RestoreBackupAsync(backupPath, backupPassword);
                    if (((CheckBox)automatic.FindName("StartWithWindowsInput")).IsChecked != false || ((CheckBox)automatic.FindName("AutoStartServerInput")).IsChecked != false || registration.Command != null)
                        throw new Exception("Restore did not disable automatic startup.");
                    if (((DataGrid)automatic.FindName("UsersGrid")).Items.Count != 1 || !((TextBox)automatic.FindName("FingerprintText")).Text.StartsWith("SHA256:"))
                        throw new Exception("Restored accounts or host fingerprint did not refresh in the UI.");
                    automatic.Close();
                    Console.WriteLine("PASS: Restore is blocked while running, refreshes accounts and host identity when stopped, and disables startup.");

                    var publicKey = new FxSsh.Algorithms.EcdsaKey("nistp256", "");
                    var keyText = "ecdsa-sha2-nistp256 " + Convert.ToBase64String(publicKey.CreateKeyAndCertificatesData());
                    var keyDialog = new UserWindow(null);
                    _ = keyDialog.Dispatcher.BeginInvoke(() =>
                    {
                        ((TextBox)keyDialog.FindName("UsernameInput")).Text = "key-only";
                        ((TextBox)keyDialog.FindName("FolderInput")).Text = home;
                        ((CheckBox)keyDialog.FindName("PasswordAuthenticationInput")).IsChecked = false;
                        ((TextBox)keyDialog.FindName("AuthorizedKeysInput")).Text = keyText;
                        keyDialog.UpdateLayout(); Capture(keyDialog, "artifacts/key-user-dialog.png");
                        ((Button)keyDialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (keyDialog.Result == null) keyDialog.Close();
                    });
                    if (keyDialog.ShowDialog() != true || keyDialog.Result is not { PasswordAuthenticationEnabled: false } || keyDialog.Result.AuthorizedKeys.Count != 1)
                        throw new Exception("The account dialog could not create a key-only account.");
                    Console.WriteLine("PASS: The account dialog creates a validated key-only account without a password.");
                    await MissingFolderUiChecks(data, settings.Users[0].PasswordHash);

                }
                catch (Exception ex) { failure = ex; }
                finally { app.Shutdown(); app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("UI smoke check timed out.");
        Console.WriteLine("UI dispatcher exited.");
        if (failure != null) throw new Exception("UI smoke check failed", failure);
    }
    private static async Task MissingFolderUiChecks(string parent, string hash)
    {
        var root = Path.Combine(parent, "home-validation");
        var good = Path.Combine(root, "valid-home"); Directory.CreateDirectory(good);
        var missingOne = Path.Combine(root, "missing-one"); var missingTwo = Path.Combine(root, "missing-two");
        var store = new SettingsStore(Path.Combine(root, "config")); PrivateStorage.Prepare(store.DataDirectory);
        using var portProbe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); portProbe.Start(); var port = ((System.Net.IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        var settings = new ServerSettings { ListenAddress = "127.0.0.1", AutoDetectListenAddress = false, Port = port, Users = [
            new SftpUser { Username = "existing", HomeDirectory = good, PasswordHash = hash },
            new SftpUser { Username = "moved-one", HomeDirectory = missingOne, PasswordHash = hash },
            new SftpUser { Username = "moved-two", HomeDirectory = missingTwo, PasswordHash = hash }
        ] };
        store.Save(settings, false);
        var backup = Path.Combine(root, "missing-folders.sftpbak");
        await Task.Run(() => BackupService.Export(store, backup, "missing-folder-backup-942!"));
        var window = new MainWindow(store.DataDirectory, new MemoryStartup()); window.Show();
        var banner = (Border)window.FindName("FolderProblemBanner");
        var grid = (DataGrid)window.FindName("UsersGrid");
        await Until(() => banner.Visibility == Visibility.Visible);
        if (!((TextBlock)window.FindName("FolderErrorText")).Text.Contains("moved-one") || !((TextBlock)window.FindName("FolderErrorText")).Text.Contains("moved-two"))
            throw new Exception("Startup warning did not identify missing accounts.");
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
        var invalidPath = Descendants<TextBlock>(grid).FirstOrDefault(text => text.Text == missingOne);
        if (invalidPath == null || invalidPath.Foreground is not SolidColorBrush color || color.Color != Color.FromRgb(165, 43, 56) || invalidPath.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) != true)
            throw new Exception("Missing home folder was not rendered red and struck through.");
        Capture(window, "artifacts/missing-home-folders.png");
        Console.WriteLine("PASS: Startup identifies missing home folders and renders their names red with a strikethrough.");

        ((Button)window.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => ((Button)window.FindName("StartButton")).IsEnabled && ((TextBlock)window.FindName("FooterText")).Text.Contains("Server not started"));
        if (!((TextBlock)window.FindName("StatusBadge")).Text.Contains("Stopped")) throw new Exception("Server started with missing enabled-account folders.");
        Console.WriteLine("PASS: Start is blocked with an actionable inline error when enabled accounts have unavailable folders.");
        ((CheckBox)window.FindName("AutoStartServerInput")).IsChecked = true;
        await window.ApplyLaunchOptionsAsync(true);
        if (!window.IsVisible || window.IsTrayIconVisible || !((TextBlock)window.FindName("StatusBadge")).Text.Contains("Stopped")) throw new Exception("Automatic startup hid a missing-folder error or started an invalid server.");
        Console.WriteLine("PASS: Automatic startup leaves missing-folder errors visible and the server stopped.");
        window.WindowState = WindowState.Minimized;
        window.TrayMenu!.ServerAction.PerformClick();
        await Until(() => window.IsVisible && ((Button)window.FindName("StartButton")).IsEnabled);
        if (((TabControl)window.FindName("MainTabs")).SelectedIndex != 0 || banner.Visibility != Visibility.Visible) throw new Exception("Tray startup did not restore the Users page and folder error.");
        Console.WriteLine("PASS: Tray startup restores the Users page to explain missing home folders.");

        await EditFolderUser(window, "moved-one", good, true);
        if (store.Load().Users.Single(u => u.Username == "moved-one").HomeDirectory != good || !((SftpUser)grid.Items[2]).IsHomeDirectoryMissing)
            throw new Exception("Repairing one account failed while another folder was still missing.");
        Console.WriteLine("PASS: Editing a folder saves the repaired account while other accounts still need attention.");
        Directory.CreateDirectory(missingTwo);
        ((Button)window.FindName("RecheckFoldersButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => banner.Visibility == Visibility.Collapsed);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        var repairedPath = Descendants<TextBlock>(grid).FirstOrDefault(text => text.Text == missingTwo);
        if (repairedPath == null || repairedPath.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true)
            throw new Exception("Restoring a folder did not clear its strikethrough.");
        Console.WriteLine("PASS: Rechecking restored folders clears the error banner and strikethrough without restarting the app.");
        Directory.Delete(good);
        ((Button)window.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => ((Button)window.FindName("StartButton")).IsEnabled && banner.Visibility == Visibility.Visible);
        if (!((SftpUser)grid.Items[0]).IsHomeDirectoryMissing || !((TextBlock)window.FindName("StatusBadge")).Text.Contains("Stopped")) throw new Exception("Pre-start check missed a folder deleted after startup.");
        Console.WriteLine("PASS: Starting rechecks folders and detects directories removed since the app opened.");
        await EditFolderUser(window, "existing", good, false);
        await EditFolderUser(window, "moved-one", good, false);
        ((Button)window.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => ((TextBlock)window.FindName("StatusBadge")).Text.Contains("Running"));
        if (banner.Visibility != Visibility.Visible || !((TextBlock)window.FindName("FolderErrorText")).Text.Contains("disabled")) throw new Exception("Disabled accounts lost their warning or blocked valid accounts.");
        Console.WriteLine("PASS: Disabled accounts retain missing-folder warnings while other enabled users can start.");
        ((Button)window.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => ((Button)window.FindName("StartButton")).IsEnabled && ((TextBlock)window.FindName("StatusBadge")).Text.Contains("Stopped"));
        await window.RestoreBackupAsync(backup, "missing-folder-backup-942!");
        if (banner.Visibility != Visibility.Visible || !grid.Items.Cast<SftpUser>().Any(u => u.Enabled && u.IsHomeDirectoryMissing)) throw new Exception("Restoring moved accounts did not check their folders.");
        Console.WriteLine("PASS: Backup restore immediately highlights unavailable folders on the destination computer.");
        window.Close();
    }

    private static async Task EditFolderUser(MainWindow window, string username, string folder, bool enabled)
    {
        var grid = (DataGrid)window.FindName("UsersGrid");
        grid.SelectedItem = grid.Items.Cast<SftpUser>().Single(u => u.Username == username);
        _ = window.Dispatcher.BeginInvoke(() =>
        {
            var dialog = Application.Current.Windows.OfType<UserWindow>().Single();
            ((TextBox)dialog.FindName("FolderInput")).Text = folder;
            ((CheckBox)dialog.FindName("EnabledInput")).IsChecked = enabled;
            ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (dialog.Result == null) dialog.Close();
        });
        ((Button)window.FindName("EditButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => grid.Items.Cast<SftpUser>().Any(u => u.Username == username && u.HomeDirectory == folder && u.Enabled == enabled));
        await window.CheckHomeFoldersAsync();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class MemoryStartup : IStartupRegistration
    {
        internal string? Command;
        public string? Read() => Command;
        public void Write(string? command) => Command = command;
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static void AssertInside(Window window, FrameworkElement element)
    {
        var content = (FrameworkElement)window.Content;
        var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Left < -1 || bounds.Top < -1 || bounds.Right > content.ActualWidth + 1 || bounds.Bottom > content.ActualHeight + 1)
            throw new Exception("Control is clipped: " + element.Name);
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}





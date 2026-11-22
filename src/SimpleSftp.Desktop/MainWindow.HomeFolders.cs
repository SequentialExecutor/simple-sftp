using System.IO;
using System.Windows;
using SimpleSftp.Core;

namespace SimpleSftp.Desktop;

public partial class MainWindow
{
    private readonly SemaphoreSlim homeFolderChecks = new(1, 1);
    private bool initialHomeFolderCheckStarted;
    private bool checkingHomeFolders;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!initialHomeFolderCheckStarted) await CheckHomeFoldersAsync();
    }

    internal async Task<bool> CheckHomeFoldersAsync()
    {
        initialHomeFolderCheckStarted = true;
        await homeFolderChecks.WaitAsync();
        try
        {
            if (closing) return false;
            checkingHomeFolders = true;
            RecheckFoldersButton.IsEnabled = false;
            var snapshot = users.Select(user => (User: user, Path: user.HomeDirectory)).ToArray();
            // Offline network folders may be slow: keep the window responsive.
            var checkedFolders = await Task.Run(() => snapshot.Select(item => (item.User, item.Path, Available: Directory.Exists(item.Path))).ToArray());
            if (closing) return false;
            foreach (var item in checkedFolders)
                if (users.Contains(item.User) && item.User.HomeDirectory == item.Path)
                    item.User.SetHomeDirectoryAvailability(item.Available);
            UpdateFolderWarning();
            return !users.Any(user => user.Enabled && user.IsHomeDirectoryMissing);
        }
        finally
        {
            checkingHomeFolders = false;
            if (!closing) RecheckFoldersButton.IsEnabled = !busy;
            homeFolderChecks.Release();
        }
    }

    private void UpdateFolderWarning()
    {
        var missing = users.Where(user => user.IsHomeDirectoryMissing).ToArray();
        FolderProblemBanner.Visibility = missing.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UsersDescription.Visibility = missing.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (missing.Length == 0) { FolderErrorText.Text = ""; return; }
        var names = string.Join(", ", missing.Take(3).Select(user => user.Username));
        if (missing.Length > 3) names += $" (+{missing.Length - 3} more)";
        FolderErrorText.Text = $"Home folders missing or unavailable for: {names}. " +
            (missing.Any(user => user.Enabled)
                ? "Edit these users to choose existing folders, or disable the accounts before starting."
                : "These accounts are disabled. Choose valid folders before enabling them; other users can still start.");
    }

    private async void RecheckFolders_Click(object sender, RoutedEventArgs e)
    {
        if (checkingHomeFolders || busy) return;
        var ready = await CheckHomeFoldersAsync();
        FooterText.Text = ready ? "Enabled accounts' home folders are available." : "Some home folders are missing or unavailable.";
    }

    private void ShowHomeFolderError()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) RestoreFromTray();
        MainTabs.SelectedIndex = 0;
        FooterText.Text = "Server not started: fix or disable accounts with unavailable home folders.";
    }
}

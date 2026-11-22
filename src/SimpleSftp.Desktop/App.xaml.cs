using System.IO;
using System.Threading;
using System.Windows;
using SimpleSftp.Core;

namespace SimpleSftp.Desktop;
public partial class App : Application
{
    private Mutex? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\SimpleSftp.Desktop", out var created);
        if (!created) { MessageBox.Show("Simple SFTP is already open.", "Simple SFTP"); Shutdown(); return; }
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleSftp");
            var launch = LaunchOptions.Parse(e.Args, data);
            PrivateStorage.Prepare(launch.DataDirectory);
            var window = new MainWindow(launch.DataDirectory);
            window.Show();
            await window.ApplyLaunchOptionsAsync(launch.WindowsStartup);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Simple SFTP could not open", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}



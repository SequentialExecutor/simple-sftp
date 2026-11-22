using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace SimpleSftp.Desktop;

internal sealed class TrayIcon : IDisposable
{
    private readonly Icon icon;
    private readonly Forms.NotifyIcon notification;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Forms.ToolStripMenuItem status;
    private readonly Forms.ToolStripMenuItem exit;
    internal Forms.ToolStripMenuItem ServerAction { get; }

    internal TrayIcon(Action restoreWindow, Action exitApplication, Func<Task> toggleServer)
    {
        using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/SimpleSftp.Desktop;component/Assets/SimpleSftp.ico"))!.Stream;
        using var source = new Icon(resource);
        icon = (Icon)source.Clone();
        menu = new Forms.ContextMenuStrip();
        status = new Forms.ToolStripMenuItem("Server stopped") { Enabled = false };
        var open = new Forms.ToolStripMenuItem("Open Simple SFTP");
        open.Click += (_, _) => restoreWindow();
        ServerAction = new Forms.ToolStripMenuItem("Start server");
        ServerAction.Click += async (_, _) => await toggleServer();
        exit = new Forms.ToolStripMenuItem("Exit Simple SFTP");
        exit.Click += (_, _) => exitApplication();
        menu.Items.AddRange([status, ServerAction, new Forms.ToolStripSeparator(), open, exit]);
        notification = new Forms.NotifyIcon { Icon = icon, Text = "Simple SFTP - Server stopped", ContextMenuStrip = menu };
        notification.DoubleClick += (_, _) => restoreWindow();
    }

    internal bool Visible { get => notification.Visible; set => notification.Visible = value; }

    internal void Update(bool running, int connections, bool busy)
    {
        var message = running ? $"Server running - {connections} connected" : "Server stopped";
        status.Text = message;
        notification.Text = "Simple SFTP - " + message;
        ServerAction.Text = running ? "Stop server" : "Start server";
        ServerAction.Enabled = !busy;
        exit.Enabled = !busy;
    }

    public void Dispose()
    {
        notification.Visible = false;
        notification.Dispose();
        menu.Dispose();
        icon.Dispose();
    }
}

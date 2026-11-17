using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TrMarketplaceHubDesktop;

/// <summary>Windows notification-area adapter. WPF remains the only application/message loop.</summary>
public sealed class MonoBridgeTrayIcon : IDisposable
{
    readonly BackgroundAppController controller;
    readonly Dispatcher dispatcher;
    readonly Forms.NotifyIcon icon;
    readonly Forms.ToolStripMenuItem pauseItem;
    readonly Action open;
    readonly Func<Task> exit;
    bool disposed;

    public MonoBridgeTrayIcon(BackgroundAppController controller, Dispatcher dispatcher, Action open, Func<Task> exit)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.open = open ?? throw new ArgumentNullException(nameof(open));
        this.exit = exit ?? throw new ArgumentNullException(nameof(exit));

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Item("MonoBridge'i aç", (_, _) => Dispatch(open)));
        menu.Items.Add(Item("Şimdi senkronize et", async (_, _) => await RunNowAsync()));
        pauseItem = Item("Senkronu duraklat", (_, _) => Dispatch(TogglePause));
        menu.Items.Add(pauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Programdan çık", async (_, _) => await ExitAsync()));
        icon = new Forms.NotifyIcon
        {
            Text = "MonoBridge",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => Dispatch(open);
        controller.StateChanged += Controller_StateChanged;
        controller.NotificationRequested += Controller_NotificationRequested;
        UpdateState();
    }

    static Forms.ToolStripMenuItem Item(string text, EventHandler click)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += click;
        return item;
    }

    void Dispatch(Action action)
    {
        if (disposed) return;
        if (dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action);
    }

    async Task RunNowAsync()
    {
        try { await controller.RunNowAsync().ConfigureAwait(false); }
        catch (Exception error) { ShowError(AuditStore.Sanitize(error.Message)); }
    }

    void TogglePause()
    {
        if (controller.State == BackgroundAppState.Paused) controller.Resume(); else controller.Pause();
    }

    async Task ExitAsync()
    {
        try { await exit().ConfigureAwait(false); }
        catch (Exception error) { ShowError(AuditStore.Sanitize(error.Message)); }
    }

    void Controller_StateChanged(object? sender, EventArgs e) => Dispatch(UpdateState);
    void Controller_NotificationRequested(object? sender, string message) => ShowError(message);
    void UpdateState() => pauseItem.Text = controller.State == BackgroundAppState.Paused ? "Senkrona devam et" : "Senkronu duraklat";

    public void ShowBackgroundNotice() => Show("MonoBridge", "MonoBridge arka planda çalışıyor", Forms.ToolTipIcon.Info);
    public void ShowError(string message) => Show("MonoBridge arka plan işlemi", message, Forms.ToolTipIcon.Warning);

    void Show(string title, string message, Forms.ToolTipIcon kind) => Dispatch(() =>
    {
        if (disposed) return;
        icon.BalloonTipTitle = title;
        icon.BalloonTipText = message.Length <= BackgroundAppController.MaxNotificationLength ? message : message[..BackgroundAppController.MaxNotificationLength];
        icon.BalloonTipIcon = kind;
        icon.ShowBalloonTip(4000);
    });

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        controller.StateChanged -= Controller_StateChanged;
        controller.NotificationRequested -= Controller_NotificationRequested;
        icon.Visible = false;
        icon.Dispose();
    }
}

using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Neuroset.Application;
using System.Runtime.InteropServices;

namespace Neuroset.Platform.Windows;

public sealed class WindowsNotificationService : INotificationService, IDisposable
{
    private readonly bool _registered;
    public WindowsNotificationService()
    {
        try { AppNotificationManager.Default.Register(); _registered = true; } catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or COMException) { _registered = false; }
    }
    public Task ShowAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_registered)
        {
            var notification = new AppNotificationBuilder().AddText(title).AddText(message).BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        return Task.CompletedTask;
    }
    public void Dispose() { if (_registered) { try { AppNotificationManager.Default.Unregister(); } catch (InvalidOperationException) { } } }
}

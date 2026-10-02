using Android.App;
using Android.Content;
using Android.OS;
using Neuroset.Application;

namespace Neuroset.App.Droid;

public sealed class AndroidNotificationService : INotificationService
{
    private const string Channel = "neuroset-agent";
    private static int _notificationId;

    public Task ShowAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = global::Android.App.Application.Context;
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            context.CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != global::Android.Content.PM.Permission.Granted)
        {
            MainActivity.Current?.RunOnUiThread(() => MainActivity.Current?.RequestPermissions([global::Android.Manifest.Permission.PostNotifications], 102));
            throw new InvalidOperationException("Allow Neuroset notifications in Android settings, then retry the notification.");
        }
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        using var channel = new NotificationChannel(Channel, "Neuroset", NotificationImportance.Default);
        manager.CreateNotificationChannel(channel);
        using var intent = new Intent(context, typeof(MainActivity));
        using var pending = PendingIntent.GetActivity(context, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        using var notification = new Notification.Builder(context, Channel)
            .SetSmallIcon(Resource.Drawable.neuroset_icon)
            .SetContentTitle(title).SetContentText(message)
            .SetStyle(new Notification.BigTextStyle().BigText(message))
            .SetContentIntent(pending).SetAutoCancel(true).Build();
        manager.Notify(Interlocked.Increment(ref _notificationId), notification);
        return Task.CompletedTask;
    }
}

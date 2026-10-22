using Lidget.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Lidget.App.Services;

/// <summary>Shows Windows toast notifications. A click on a toast raises <see cref="Activated"/>.</summary>
public sealed class ToastService : IDisposable
{
    private const string Group = "lidget";

    private readonly ILogger _logger;

    public ToastService(ILogger logger)
    {
        _logger = logger;
        ToastNotificationManagerCompat.OnActivated += OnActivated;
    }

    /// <summary>Raised on a background thread when the user clicks a Lidget toast.</summary>
    public event EventHandler? Activated;

    public void Show(UsageNotification notification)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument("action", "open")
                .AddText(notification.Title)
                .AddText(notification.Body)
                .Show(toast =>
                {
                    // A newer notice for the same limit replaces the older one in the notification center.
                    toast.Tag = notification.Tag.Length <= 64 ? notification.Tag : notification.Tag[..64];
                    toast.Group = Group;
                });
            _logger.LogInformation("Showed notification: {Title}", notification.Title);
        }
        catch (Exception ex)
        {
            // Notifications can be off for the app or for the system. That must not stop Lidget.
            _logger.LogWarning(ex, "Could not show the notification {Title}", notification.Title);
        }
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        _logger.LogDebug("A notification was clicked");
        Activated?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => ToastNotificationManagerCompat.OnActivated -= OnActivated;
}

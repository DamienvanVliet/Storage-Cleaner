using System.Windows;

namespace StorageCleaner.App.Services;

public sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message, bool warning = false)
    {
        return ShowOnUiThread(() =>
        {
            var dialog = new ThemedDialogWindow(title, message, ThemedDialogButtons.YesNo, warning)
            {
                Owner = ResolveOwner()
            };
            return dialog.ShowDialog() == true;
        });
    }

    public bool ConfirmTyped(string title, string message, string expectedText, bool warning = true)
    {
        return ShowOnUiThread(() =>
        {
            var dialog = new ThemedDialogWindow(
                title,
                message,
                ThemedDialogButtons.TypedConfirm,
                warning,
                expectedText)
            {
                Owner = ResolveOwner()
            };
            return dialog.ShowDialog() == true;
        });
    }

    public void ShowInfo(string title, string message)
    {
        ShowOnUiThread(() =>
        {
            var dialog = new ThemedDialogWindow(title, message, ThemedDialogButtons.Ok, warning: false)
            {
                Owner = ResolveOwner()
            };
            dialog.ShowDialog();
            return true;
        });
    }

    public void ShowError(string title, string message)
    {
        ShowOnUiThread(() =>
        {
            var dialog = new ThemedDialogWindow(title, message, ThemedDialogButtons.Ok, warning: true)
            {
                Owner = ResolveOwner()
            };
            dialog.ShowDialog();
            return true;
        });
    }

    private static T ShowOnUiThread<T>(Func<T> callback)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return callback();
        }

        return dispatcher.Invoke(callback);
    }

    private static Window? ResolveOwner()
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }

        var active = app.Windows.OfType<Window>().FirstOrDefault(static window => window.IsActive);
        return active ?? app.MainWindow;
    }
}

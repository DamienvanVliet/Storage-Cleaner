namespace StorageCleaner.App.Services;

public interface IDialogService
{
    bool Confirm(string title, string message, bool warning = false);

    bool ConfirmTyped(string title, string message, string expectedText, bool warning = true);

    void ShowInfo(string title, string message);

    void ShowError(string title, string message);
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GcsFileViewer.ViewModels;

/// <summary>The two in-window dialogs: a yes/no confirmation, and a prompt for a name.</summary>
public partial class DialogsViewModel : ObservableObject
{
    private TaskCompletionSource<bool>? _confirmation;
    private TaskCompletionSource<string?>? _prompt;
    private Func<string, Task<string?>>? _promptValidator;

    [ObservableProperty]
    private bool _isConfirmVisible;

    [ObservableProperty]
    private string _confirmTitle = "";

    [ObservableProperty]
    private string _confirmMessage = "";

    [ObservableProperty]
    private string _confirmAction = "";

    [ObservableProperty]
    private bool _isPromptVisible;

    [ObservableProperty]
    private string _promptTitle = "";

    [ObservableProperty]
    private string _promptText = "";

    [ObservableProperty]
    private string _promptAction = "";

    [ObservableProperty]
    private string? _promptError;

    /// <summary>Asks a question and completes with true if the user accepts. Asking again dismisses any earlier question.</summary>
    /// <param name="action">The label of the button that accepts, such as "Delete".</param>
    public Task<bool> ConfirmAsync(string title, string message, string action)
    {
        CloseConfirmation(false);
        ConfirmTitle = title;
        ConfirmMessage = message;
        ConfirmAction = action;
        IsConfirmVisible = true;
        _confirmation = new TaskCompletionSource<bool>();
        return _confirmation.Task;
    }

    /// <summary>
    /// Asks for a name and completes with it, trimmed, or with null if the user cancels. The prompt stays open,
    /// showing the validator's message, until the validator returns null for what was typed.
    /// </summary>
    public Task<string?> PromptAsync(string title, string initial, string action, Func<string, Task<string?>> validate)
    {
        ClosePrompt(null);
        PromptTitle = title;
        PromptText = initial;
        PromptAction = action;
        PromptError = null;
        _promptValidator = validate;
        _prompt = new TaskCompletionSource<string?>();
        IsPromptVisible = true;
        return _prompt.Task;
    }

    [RelayCommand]
    private void AcceptConfirm() => CloseConfirmation(true);

    [RelayCommand]
    private void RejectConfirm() => CloseConfirmation(false);

    [RelayCommand]
    private async Task AcceptPromptAsync()
    {
        if (_prompt is not { } prompt || _promptValidator is not { } validate)
        {
            return;
        }

        var text = PromptText.Trim();
        var error = await validate(text);

        // The prompt may have been cancelled or replaced while the name was being checked.
        if (_prompt != prompt)
        {
            return;
        }

        PromptError = error;
        if (error is null)
        {
            ClosePrompt(text);
        }
    }

    [RelayCommand]
    private void RejectPrompt() => ClosePrompt(null);

    private void CloseConfirmation(bool accepted)
    {
        var confirmation = _confirmation;
        _confirmation = null;
        IsConfirmVisible = false;
        confirmation?.TrySetResult(accepted);
    }

    private void ClosePrompt(string? result)
    {
        var prompt = _prompt;
        _prompt = null;
        _promptValidator = null;
        IsPromptVisible = false;
        prompt?.TrySetResult(result);
    }
}

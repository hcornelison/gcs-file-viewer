using GcsFileViewer.ViewModels;

namespace GcsFileViewer.Tests;

public class DialogsViewModelTests
{
    private readonly DialogsViewModel _dialogs = new();

    private static Task<string?> Accept(string text) => Task.FromResult<string?>(null);

    [Fact]
    public async Task A_confirmation_shows_its_text_and_completes_with_the_answer()
    {
        var answer = _dialogs.ConfirmAsync("Delete it?", "This can't be undone.", "Delete");

        Assert.True(_dialogs.IsConfirmVisible);
        Assert.Equal("Delete it?", _dialogs.ConfirmTitle);
        Assert.Equal("This can't be undone.", _dialogs.ConfirmMessage);
        Assert.Equal("Delete", _dialogs.ConfirmAction);
        Assert.False(answer.IsCompleted);

        _dialogs.AcceptConfirmCommand.Execute(null);

        Assert.True(await answer);
        Assert.False(_dialogs.IsConfirmVisible);
    }

    [Fact]
    public async Task Rejecting_a_confirmation_completes_it_with_false()
    {
        var answer = _dialogs.ConfirmAsync("Sure?", "", "Yes");

        _dialogs.RejectConfirmCommand.Execute(null);

        Assert.False(await answer);
    }

    [Fact]
    public async Task Asking_again_dismisses_the_earlier_question_as_declined()
    {
        var first = _dialogs.ConfirmAsync("First?", "", "Yes");
        var second = _dialogs.ConfirmAsync("Second?", "", "Yes");

        Assert.False(await first);
        Assert.Equal("Second?", _dialogs.ConfirmTitle);
        Assert.True(_dialogs.IsConfirmVisible);

        _dialogs.AcceptConfirmCommand.Execute(null);
        Assert.True(await second);
    }

    [Fact]
    public void Answering_when_nothing_is_asked_does_nothing()
    {
        _dialogs.AcceptConfirmCommand.Execute(null);
        _dialogs.RejectConfirmCommand.Execute(null);
        _dialogs.AcceptPromptCommand.Execute(null);
        _dialogs.RejectPromptCommand.Execute(null);

        Assert.False(_dialogs.IsConfirmVisible);
        Assert.False(_dialogs.IsPromptVisible);
    }

    [Fact]
    public async Task A_prompt_starts_from_the_given_text_and_completes_with_what_was_typed_trimmed()
    {
        var name = _dialogs.PromptAsync("Rename", "old.txt", "Rename", Accept);

        Assert.True(_dialogs.IsPromptVisible);
        Assert.Equal("Rename", _dialogs.PromptTitle);
        Assert.Equal("old.txt", _dialogs.PromptText);
        Assert.Equal("Rename", _dialogs.PromptAction);
        Assert.Null(_dialogs.PromptError);

        _dialogs.PromptText = "  new.txt ";
        await _dialogs.AcceptPromptCommand.ExecuteAsync(null);

        Assert.Equal("new.txt", await name);
        Assert.False(_dialogs.IsPromptVisible);
    }

    [Fact]
    public async Task A_prompt_stays_open_until_the_text_is_valid()
    {
        var name = _dialogs.PromptAsync("Name", "", "OK", text => Task.FromResult(text.Length < 3 ? "Too short." : null));

        _dialogs.PromptText = "ab";
        await _dialogs.AcceptPromptCommand.ExecuteAsync(null);

        Assert.Equal("Too short.", _dialogs.PromptError);
        Assert.True(_dialogs.IsPromptVisible);
        Assert.False(name.IsCompleted);

        _dialogs.PromptText = "abc";
        await _dialogs.AcceptPromptCommand.ExecuteAsync(null);

        Assert.Null(_dialogs.PromptError);
        Assert.Equal("abc", await name);
    }

    [Fact]
    public async Task Cancelling_a_prompt_completes_it_with_nothing()
    {
        var name = _dialogs.PromptAsync("Name", "x", "OK", Accept);

        _dialogs.RejectPromptCommand.Execute(null);

        Assert.Null(await name);
        Assert.False(_dialogs.IsPromptVisible);
    }

    [Fact]
    public async Task Cancelling_while_a_name_is_being_checked_wins_over_the_check()
    {
        var check = new TaskCompletionSource<string?>();
        var name = _dialogs.PromptAsync("Name", "x", "OK", _ => check.Task);
        var accepting = _dialogs.AcceptPromptCommand.ExecuteAsync(null);

        _dialogs.RejectPromptCommand.Execute(null);
        check.SetResult(null);
        await accepting;

        Assert.Null(await name);
        Assert.False(_dialogs.IsPromptVisible);
        Assert.Null(_dialogs.PromptError);
    }

    [Fact]
    public async Task Prompting_again_cancels_the_earlier_prompt()
    {
        var first = _dialogs.PromptAsync("First", "a", "OK", Accept);
        var second = _dialogs.PromptAsync("Second", "b", "OK", Accept);

        Assert.Null(await first);
        Assert.Equal("b", _dialogs.PromptText);

        await _dialogs.AcceptPromptCommand.ExecuteAsync(null);
        Assert.Equal("b", await second);
    }
}

using GcsFileViewer.ViewModels;

namespace GcsFileViewer.Tests;

public class NavigationHistoryTests
{
    private static readonly Location A = new("a", "");
    private static readonly Location B = new("a", "docs/");
    private static readonly Location C = new("b", "");

    private readonly NavigationHistory _history = new();

    [Fact]
    public void It_starts_at_the_bucket_list_with_nowhere_to_go()
    {
        Assert.Equal(Location.Root, _history.Current);
        Assert.False(_history.CanGoBack);
        Assert.False(_history.CanGoForward);
    }

    [Fact]
    public void Navigating_makes_the_old_location_the_one_to_go_back_to()
    {
        Assert.True(_history.Navigate(A));

        Assert.Equal(A, _history.Current);
        Assert.True(_history.CanGoBack);
        Assert.Equal(Location.Root, _history.Back());
        Assert.False(_history.CanGoBack);
    }

    [Fact]
    public void Navigating_to_where_it_already_is_changes_nothing()
    {
        _history.Navigate(A);

        Assert.False(_history.Navigate(new Location("a", "")));

        Assert.Equal(Location.Root, _history.Back());
        Assert.False(_history.CanGoBack);
    }

    [Fact]
    public void Back_and_forward_walk_the_same_path_in_both_directions()
    {
        _history.Navigate(A);
        _history.Navigate(B);
        _history.Navigate(C);

        Assert.Equal(B, _history.Back());
        Assert.Equal(A, _history.Back());
        Assert.True(_history.CanGoForward);
        Assert.Equal(B, _history.Forward());
        Assert.Equal(C, _history.Forward());
        Assert.False(_history.CanGoForward);
        Assert.Equal(C, _history.Current);
    }

    [Fact]
    public void Navigating_after_going_back_discards_what_was_ahead()
    {
        _history.Navigate(A);
        _history.Navigate(B);
        _history.Back();

        _history.Navigate(C);

        Assert.False(_history.CanGoForward);
        Assert.Equal(A, _history.Back());
    }

    [Fact]
    public void A_search_is_a_location_of_its_own()
    {
        _history.Navigate(A);

        Assert.True(_history.Navigate(A with { Search = "pdf" }));

        Assert.Equal(A, _history.Back());
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        _history.Navigate(A);
        _history.Navigate(B);
        _history.Back();

        _history.Reset(C);

        Assert.Equal(C, _history.Current);
        Assert.False(_history.CanGoBack);
        Assert.False(_history.CanGoForward);
    }
}

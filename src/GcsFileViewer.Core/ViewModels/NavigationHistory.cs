namespace GcsFileViewer.ViewModels;

/// <summary>The current location and the ones to go back and forward to, as in a web browser.</summary>
public sealed class NavigationHistory
{
    private readonly Stack<Location> _back = new();
    private readonly Stack<Location> _forward = new();

    public Location Current { get; private set; } = Location.Root;

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Goes somewhere new, which discards anything there was to go forward to.</summary>
    /// <returns>False if that is where the browser already is, in which case nothing changes.</returns>
    public bool Navigate(Location location)
    {
        if (location == Current)
        {
            return false;
        }

        _back.Push(Current);
        _forward.Clear();
        Current = location;
        return true;
    }

    public Location Back()
    {
        _forward.Push(Current);
        return Current = _back.Pop();
    }

    public Location Forward()
    {
        _back.Push(Current);
        return Current = _forward.Pop();
    }

    /// <summary>Starts again from a location with nothing to go back or forward to.</summary>
    public void Reset(Location location)
    {
        _back.Clear();
        _forward.Clear();
        Current = location;
    }
}

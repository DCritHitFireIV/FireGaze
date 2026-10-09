namespace FireGaze.UI;

internal sealed class MainTabSelection
{
    private MainTab? pending;

    public void Request(MainTab tab) => pending = tab;
    public void RequestDefault(MainTab tab) => pending ??= tab;

    public MainTab? Take()
    {
        var result = pending;
        pending = null;
        return result;
    }
}

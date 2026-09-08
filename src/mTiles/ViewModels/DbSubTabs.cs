namespace mTiles.ViewModels;

/// <summary>
/// Which of the Database page's three lists is showing.
/// </summary>
/// <remarks>
/// <para>Named for the reason <see cref="SettingsTabs"/> is, and this strip is where that reasoning was
/// still owed: its two buttons carried boxed <c>Zero</c>/<c>One</c> resources, so inserting a page in
/// the middle moved every one of them by hand.</para>
/// <para><b>Two lists became three</b> because they answer different questions. What this machine
/// happens to be able to see is a scan's output — it changes on its own, and nothing on it is the
/// user's work. A manual connection is something they typed, with a password in it, and it is the only
/// route to a database the scan cannot reach. Sharing one page, the typed rows sat above a list that
/// rewrites itself, and the two heading rows' actions — Add and Rescan — read as alternatives to each
/// other.</para>
/// </remarks>
public static class DbSubTabs
{
    public const int Config = 0;
    public const int Discovered = 1;
    public const int Manual = 2;
}

namespace mTiles.ViewModels;

/// <summary>
/// Which page of the Settings dialog is showing.
/// </summary>
/// <remarks>
/// <para>Named because the numbers had spread. The same <c>3</c> appeared in five places — three
/// branches deciding what to load on arrival, one <c>private const</c> that only one of them used, and a
/// bare literal in the database tile's "open my settings" button, which is the one nobody would think to
/// change when a tab is inserted before it. That last one is the failure this prevents: a tab added in
/// the middle silently sends the button to a different page, and nothing anywhere is wrong enough to
/// notice.</para>
/// <para>Constants rather than an <c>enum</c>, deliberately. The selection is bound as an <c>int</c> to
/// button command parameters in two AXAML files and to a source-generated observable property; an enum
/// buys type safety over a value nothing computes — every write is one of these five names — in exchange
/// for touching every binding. The numbers were the problem, not the type.</para>
/// </remarks>
public static class SettingsTabs
{
    public const int General = 0;
    public const int Profiles = 1;
    public const int AiTools = 2;
    public const int Database = 3;
    public const int Speech = 4;
}

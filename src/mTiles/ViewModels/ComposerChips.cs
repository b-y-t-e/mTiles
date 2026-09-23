using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.AgentSessions;

namespace mTiles.ViewModels;

/// <summary>
/// The chips above a composer — one strip of images or of files, in the order the text names them.
/// </summary>
/// <remarks>One class for both composers and both kinds of chip, because each strip is redrawn from the
/// text on every keystroke and four hand-kept copies of that rule are four chances to drift apart.</remarks>
public sealed class ComposerChips<T> : ObservableObject
{
    public ObservableCollection<T> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>Shows <paramref name="items"/>; an unchanged list is left alone, so a chip is not redrawn
    /// on every keystroke.</summary>
    public void Show(IReadOnlyList<T> items)
    {
        if (items.SequenceEqual(Items)) return;
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(nameof(HasItems));
    }
}

/// <summary>Which of the images waiting beside a composer its text still names.</summary>
public static class ComposerImageChips
{
    /// <summary>The images whose markers stand in <paramref name="text"/>, in the order the markers do.</summary>
    /// <remarks>Read off the text rather than kept beside it: the markers are what the agent is sent, so
    /// deleting one by hand takes its chip away, and undoing the deletion brings it back — the image itself
    /// stays waiting until the message goes.</remarks>
    public static IReadOnlyList<T> NamedIn<T>(string text, IEnumerable<T> waiting, Func<T, int> indexOf)
    {
        var byIndex = waiting.GroupBy(indexOf).ToDictionary(group => group.Key, group => group.First());
        return [.. ImageMarkers.InOrder(text).Where(byIndex.ContainsKey).Select(index => byIndex[index])];
    }
}

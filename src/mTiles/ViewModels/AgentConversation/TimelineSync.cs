using System.Collections.ObjectModel;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>
/// Brings a collection of view models into step with a newer list of records, touching only what
/// changed.
/// </summary>
/// <remarks>
/// <para>By position, because the reducer only ever appends to a timeline or changes an entry where it
/// is — nothing is inserted before or removed from the middle. So the first records are the ones already
/// shown, and a record that is the same object as the one last drawn has not changed at all: an immutable
/// update is a new object, which is what makes this comparison sufficient.</para>
/// <para>A shorter list than the collection means the conversation was replaced (a new conversation in
/// the same tile), and the extra view models go.</para>
/// </remarks>
public static class TimelineSync
{
    public static void Sync<TViewModel, TRecord>(ObservableCollection<TViewModel> items,
        IReadOnlyList<TRecord> records, Func<TRecord, TViewModel> create)
        where TViewModel : TimelineItemViewModel
        where TRecord : notnull
    {
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (i >= items.Count)
            {
                items.Add(create(record));
                continue;
            }

            var shown = items[i];
            if (ReferenceEquals(shown.Source, record)) continue;

            if (shown.CanShow(record)) shown.Update(record);
            else items[i] = create(record);
        }

        while (items.Count > records.Count) items.RemoveAt(items.Count - 1);
    }
}

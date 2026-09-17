namespace mTiles.Controls;

/// <summary>One row a <see cref="Picker"/> offers.</summary>
/// <remarks>
/// <para>Everything past <see cref="Title"/> is optional, and a picker draws only what it was given: a
/// permission mode is a glyph, a name and a sentence; a model is a name, the provider under it and a
/// keyboard shortcut at the far end; an effort is a name and sometimes the word Default. One row type
/// rather than one per picker, because the alternative is three lists that drift apart in their padding,
/// their highlight and their idea of what a disabled row looks like.</para>
/// <para><b>A row that cannot be picked is still a row.</b> <see cref="IsEnabled"/> false draws it dimmed
/// with <see cref="DisabledReason"/> under it and refuses the click - it is never left out of the list and
/// never handed to Avalonia as a disabled item, which drops out of the hit test and takes the very
/// sentence explaining the refusal with it.</para>
/// </remarks>
public sealed record PickerOption
{
    /// <summary>What the caller gets back. Never shown.</summary>
    public required string Id { get; init; }

    /// <summary>The row's name, and what the trigger says once it is picked.</summary>
    public required string Title { get; init; }

    /// <summary>A line under the title saying what this row means - the sentence in a permission menu.</summary>
    public string? Description { get; init; }

    /// <summary>A line under the title naming where this row comes from - the provider under a model.</summary>
    public string? Detail { get; init; }

    /// <summary>A word beside the title, for the rare exception: <c>Default</c>, <c>Unavailable</c>.</summary>
    public string? Badge { get; init; }

    /// <summary>A key that reaches this row, drawn at the far end. Said, never bound - the host binds it.</summary>
    public string? Shortcut { get; init; }

    /// <summary>The heading this row sits under. Rows are drawn in the order given and a heading is drawn
    /// when the group changes, so the caller's order is the whole of the grouping.</summary>
    public string? Group { get; init; }

    /// <summary>Which rail entry shows this row. Null means every one of them.</summary>
    public string? CategoryId { get; init; }

    /// <summary>Drawn in front of the title. An <c>object</c> so this project needs no icon library: the
    /// host passes whatever it draws icons with, and a string is drawn as a string.</summary>
    public object? Icon { get; init; }

    /// <summary>False draws the row dimmed and refuses the click. See the type's own remarks.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Why this row cannot be picked, drawn under it. Only read while <see cref="IsEnabled"/> is false.</summary>
    public string? DisabledReason { get; init; }

    /// <summary>A row that is an action rather than a place - <c>New conversation</c>. Drawn in the accent.</summary>
    public bool IsAction { get; init; }

    /// <summary>Extra words the search matches on beyond the title and the two lines under it.</summary>
    public string? Keywords { get; init; }

    /// <summary>Whatever the host wants back with the pick.</summary>
    public object? Tag { get; init; }
}

/// <summary>A heading between two runs of rows. Built by the picker from <see cref="PickerOption.Group"/>.</summary>
public sealed record PickerHeading(string Text);

/// <summary>An entry in the rail down the left of a picker: one provider, or the favourites.</summary>
public sealed record PickerCategory
{
    public required string Id { get; init; }

    /// <summary>Said in the tooltip. The rail is glyphs - it is one column of a popup wide.</summary>
    public required string Title { get; init; }

    public object? Icon { get; init; }
}

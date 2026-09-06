using Avalonia.Data.Converters;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace mTiles.Views;

/// <summary>What a thing in the Goal tile reads like once it is on the clipboard.</summary>
/// <remarks>
/// <para>One rule, in one place, used by every copy button in the tile — the one beside a message, the
/// one on a finding, the one that takes a whole review. So a finding copied on its own reads exactly
/// as it does inside the review it came from, which is the property the transcript file depends on
/// too: <see cref="GoalTranscript"/> writes both from these same methods.</para>
/// <para>A converter rather than a method on the view, because that is what let the finding template
/// stop naming a code-behind handler and become something a shared styles file can hold. It decides
/// nothing about presentation — <see cref="CopyButton"/> owns the clipboard and the tick — and nothing
/// about wording, which is <c>GoalTranscript</c>'s.</para>
/// </remarks>
public sealed class CopyableText : IValueConverter
{
    /// <summary>The one instance; it holds no state, so markup uses it through <c>x:Static</c>.</summary>
    public static readonly CopyableText Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Of(value);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A clipboard string is not read back into a finding.");

    /// <summary>The copyable text for one item of the transcript, or "" for anything else.</summary>
    /// <remarks>Empty rather than an exception for an unknown type: a copy button is a convenience,
    /// and <see cref="CopyButton"/> treats an empty string as nothing to do.</remarks>
    public static string Of(object? data) => data switch
    {
        GoalMessage message => GoalTranscript.Copyable(message),
        GoalFinding finding => GoalTranscript.Copyable(finding),
        GoalQuestion question => GoalTranscript.Copyable(question),
        IEnumerable<GoalFinding> findings => GoalTranscript.Copyable(findings),

        // The live block, where the answer is still being typed: the view model's own snapshot, which
        // is also what the record is written from, so what is copied mid-round and what is copied out
        // of the record afterwards are made by one method.
        GoalQuestionAnswer asking => GoalTranscript.Copyable(asking.Snapshot()),
        _ => "",
    };
}

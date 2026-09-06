using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using System.Diagnostics;
using System.Linq;

namespace mTiles.Views;

/// <summary>A button that puts text on the clipboard and shows a tick for a moment.</summary>
/// <remarks>
/// <para>Attached to the button in markup — <c>v:CopyButton.Text="{Binding …}"</c> — rather than wired
/// to a <c>Click</c> handler in a view's code-behind, and that is the whole reason it exists. A
/// <c>DataTemplate</c> carrying <c>Click="CopyItem_Click"</c> can only be used inside the one view
/// that declares that method, so the finding template could not be shared with a dialog drawn
/// anywhere else. This knows nothing about who hosts it.</para>
/// <para>It also knows nothing about <em>what</em> is being copied: it takes a string, and turning a
/// model object into one is <see cref="CopyableText"/>'s job. Clipboard and animation are view
/// concerns and stay here; deciding what a finding reads like is not, and lives in
/// <c>GoalTranscript</c> where the transcript file is written from the same method.</para>
/// </remarks>
public static class CopyButton
{
    /// <summary>The text this button copies. Setting it is what makes a button a copy button.</summary>
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Button, string?>("Text", typeof(CopyButton));

    public static string? GetText(Button button) => button.GetValue(TextProperty);
    public static void SetText(Button button, string? value) => button.SetValue(TextProperty, value);

    /// <summary>How long the tick stays.</summary>
    /// <remarks>Long enough to be seen, short enough that copying the next row does not find the
    /// button still congratulating itself about the last one.</remarks>
    private static readonly TimeSpan CopiedFeedback = TimeSpan.FromSeconds(1.1);

    static CopyButton() => TextProperty.Changed.AddClassHandler<Button>(OnTextChanged);

    private static void OnTextChanged(Button button, AvaloniaPropertyChangedEventArgs e)
    {
        // Subscribed once per button, however often the bound text changes underneath it — a row in a
        // list is reused as the list scrolls, and a handler added per change would copy once per
        // rebind.
        button.Click -= OnClick;
        button.Click += OnClick;
    }

    private static async void OnClick(object? sender, RoutedEventArgs e)
    {
        // async void, so nothing may escape: this returns to the dispatcher at the first await, where
        // an exception is a crash rather than a message. Failing to copy is not worth the process.
        try
        {
            if (sender is Button button)
                await CopyAsync(button, GetText(button) ?? "");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Copying failed: {ex.Message}");
        }
    }

    private static async Task CopyAsync(Button button, string text)
    {
        if (text.Length == 0) return;
        if (TopLevel.GetTopLevel(button)?.Clipboard is not { } clipboard) return;

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            // A clipboard can be held by another application. Not worth a dialog over a convenience.
            Trace.TraceWarning($"Copying failed: {ex.Message}");
            return;
        }

        if (IconIn(button.Content) is not { } icon) return;

        icon.Kind = MaterialIconKind.Check;
        await Task.Delay(CopiedFeedback);

        // Checked again: a row is reused as the list changes, so by now this button may be showing a
        // different item — and it is still the same button, so it is still the tick that has to come
        // off.
        if (icon.Kind == MaterialIconKind.Check) icon.Kind = MaterialIconKind.ContentCopy;
    }

    /// <summary>The icon a copy button answers on: its whole content, or the one beside its label.</summary>
    private static MaterialIcon? IconIn(object? content) => content switch
    {
        MaterialIcon icon => icon,
        Panel panel => panel.Children.OfType<MaterialIcon>().FirstOrDefault(),
        _ => null,
    };
}

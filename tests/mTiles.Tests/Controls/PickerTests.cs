using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using mTiles.Controls;
using mTiles.Tests.AgentSessions;
using Xunit;

namespace mTiles.Tests.Controls;

/// <summary>
/// The picker: what it offers, what it refuses, and what it draws.
/// </summary>
/// <remarks>Most of it needs no window — the rows are built from the options, the search and the rail
/// before anything is templated, which is deliberate: the part of a control worth arguing about is the part
/// that can be argued about without a renderer. The last test is the one that cannot be, and it is there
/// because a control theme that throws on realisation compiles perfectly well.</remarks>
public class PickerTests
{
    private static PickerOption Option(string id, string? group = null, bool enabled = true,
        string? category = null) =>
        new()
        {
            Id = id,
            Title = id,
            Group = group,
            IsEnabled = enabled,
            DisabledReason = enabled ? null : $"{id} cannot be picked",
            CategoryId = category,
        };

    [Fact]
    public void Every_option_is_a_row_and_a_group_becomes_a_heading()
    {
        var picker = new Picker
        {
            Options = new[]
            {
                Option("low", "Reasoning"), Option("high", "Reasoning"), Option("200k", "Context Window"),
            },
        };

        Assert.Collection(picker.Rows,
            row => Assert.Equal("Reasoning", Assert.IsType<PickerHeading>(row).Text),
            row => Assert.Equal("low", Assert.IsType<PickerRow>(row).Title),
            row => Assert.Equal("high", Assert.IsType<PickerRow>(row).Title),
            row => Assert.Equal("Context Window", Assert.IsType<PickerHeading>(row).Text),
            row => Assert.Equal("200k", Assert.IsType<PickerRow>(row).Title));
    }

    /// <summary>A heading is drawn when the group changes, so a group the filter empties takes its heading
    /// with it — the failure being avoided is a menu of headings with nothing under them.</summary>
    [Fact]
    public void A_heading_whose_rows_are_all_filtered_out_is_not_drawn()
    {
        var picker = new Picker
        {
            Options = new[] { Option("low", "Reasoning"), Option("200k", "Context Window") },
        };

        Search(picker, "low");

        Assert.Collection(picker.Rows,
            row => Assert.Equal("Reasoning", Assert.IsType<PickerHeading>(row).Text),
            row => Assert.Equal("low", Assert.IsType<PickerRow>(row).Title));
    }

    /// <summary>The whole reason the trigger and the search are two controls: opening the list must show
    /// every option, not the one the picker is already on.</summary>
    [Fact]
    public void Opening_the_list_shows_everything_however_long_the_selection_is()
    {
        var picker = new Picker
        {
            Options = new[] { Option("z-ai/glm-5.3-flash"), Option("claude-opus-5"), Option("gpt-5.5") },
            SelectedId = "z-ai/glm-5.3-flash",
        };
        Search(picker, "glm");
        Assert.Single(picker.Rows);

        picker.IsDropDownOpen = true;

        Assert.Equal(3, picker.Rows.Count);
        Assert.True(Assert.IsType<PickerRow>(picker.Rows[0]).IsSelected);
    }

    [Fact]
    public void The_search_forgives_the_punctuation_nobody_remembers()
    {
        var picker = new Picker { Options = new[] { Option("z-ai/glm-5.3-flash"), Option("gpt-5.5") } };

        Search(picker, "5.3 glm");

        Assert.Equal("z-ai/glm-5.3-flash", Assert.IsType<PickerRow>(Assert.Single(picker.Rows)).Title);
    }

    /// <summary>A caller's own rule replaces the built-in one whole — the control has an opinion, not a law.</summary>
    [Fact]
    public void A_caller_can_say_what_matching_means()
    {
        var picker = new Picker
        {
            Options = new[] { Option("alpha"), Option("beta") },
            Filter = (search, option) => option.Title.StartsWith(search ?? "", StringComparison.Ordinal),
        };

        Search(picker, "b");

        Assert.Equal("beta", Assert.IsType<PickerRow>(Assert.Single(picker.Rows)).Title);
    }

    /// <summary>The host's own models, read one line at a time, so nothing has to keep a second list in step.</summary>
    [Fact]
    public void Options_need_not_be_PickerOptions()
    {
        var picker = new Picker
        {
            Options = new object[] { 1, 2, "skip me" },
            OptionSelector = item =>
                item is int number ? new PickerOption { Id = $"{number}", Title = $"#{number}" } : null,
        };

        Assert.Equal(["#1", "#2"], picker.Rows.OfType<PickerRow>().Select(row => row.Title));
    }

    /// <summary>A refused row stays in the list, dimmed, carrying its reason — never handed to Avalonia as a
    /// disabled item, which leaves the hit test and takes that very sentence with it.</summary>
    [Fact]
    public void A_row_that_cannot_be_picked_is_shown_with_its_reason_and_refuses_the_pick()
    {
        var picker = new Picker { Options = new[] { Option("ok"), Option("busy", enabled: false) } };
        var picked = new List<string>();
        picker.SelectionRequested += (_, e) => picked.Add(e.Option.Id);
        picker.IsDropDownOpen = true;

        var refused = picker.Rows.OfType<PickerRow>().Single(row => row.Title == "busy");
        Assert.False(refused.CanBePicked);
        Assert.Equal("busy cannot be picked", refused.Reason);

        // The keyboard cannot reach it either: Down from the first pickable row has nowhere to go.
        Press(picker, Avalonia.Input.Key.Down);
        Press(picker, Avalonia.Input.Key.Enter);

        Assert.Equal(["ok"], picked);
    }

    [Fact]
    public void The_arrows_move_the_highlight_and_Enter_takes_it()
    {
        var picker = new Picker
        {
            Options = new[] { Option("a", "Group"), Option("b", "Group"), Option("c", "Group") },
        };
        var picked = new List<string>();
        picker.SelectionRequested += (_, e) => picked.Add(e.Option.Id);
        picker.IsDropDownOpen = true;

        // Opening highlights the first pickable row, and the heading between them is stepped over.
        Press(picker, Avalonia.Input.Key.Down);
        Press(picker, Avalonia.Input.Key.Down);
        Press(picker, Avalonia.Input.Key.Enter);

        Assert.Equal(["c"], picked);
    }

    /// <summary>It does not wrap. At the moment it happens, a list jumping from its end to its start looks
    /// exactly like a list that scrolled — and this one has headings, so there is nothing at the top to
    /// recognise as the top.</summary>
    [Fact]
    public void The_highlight_stops_at_the_end_rather_than_wrapping()
    {
        var picker = new Picker { Options = new[] { Option("a"), Option("b") } };
        var picked = new List<string>();
        picker.SelectionRequested += (_, e) => picked.Add(e.Option.Id);
        picker.IsDropDownOpen = true;

        for (var press = 0; press < 5; press++) Press(picker, Avalonia.Input.Key.Down);
        Press(picker, Avalonia.Input.Key.Enter);

        Assert.Equal(["b"], picked);
    }

    /// <summary>The rows are rebuilt on every keystroke, so a highlight kept from before the search points at
    /// a row that is no longer drawn - and Enter picked it.</summary>
    [Fact]
    public void Enter_after_a_search_takes_a_row_the_search_left_on_screen()
    {
        var picker = new Picker
        {
            Options = new[] { Option("claude-opus-5"), Option("z-ai/glm-5.3-flash") },
            SelectedId = "claude-opus-5",
        };
        var picked = new List<string>();
        picker.SelectionRequested += (_, e) => picked.Add(e.Option.Id);
        picker.IsDropDownOpen = true;

        Search(picker, "glm");
        Press(picker, Avalonia.Input.Key.Enter);

        Assert.Equal(["z-ai/glm-5.3-flash"], picked);
    }

    /// <summary>A catalogue is a suggestion: a model it does not list, or an agent that lists none, can still
    /// be named by typing it.</summary>
    [Fact]
    public void A_typed_entry_is_offered_only_when_no_option_already_carries_it()
    {
        var picker = new Picker { Options = new[] { Option("opus") }, TypedEntryFormat = "Use {0}" };
        var picked = new List<string>();
        picker.SelectionRequested += (_, e) => picked.Add(e.Option.Id);
        picker.IsDropDownOpen = true;

        Search(picker, "opus");
        Assert.Single(picker.Rows);

        Search(picker, " my-model ");
        Assert.Equal("Use my-model", Assert.IsType<PickerRow>(Assert.Single(picker.Rows)).Title);
        Press(picker, Avalonia.Input.Key.Enter);

        Assert.Equal(["my-model"], picked);
    }

    [Fact]
    public void Escape_closes_the_list_and_picks_nothing()
    {
        var picker = new Picker { Options = new[] { Option("a") } };
        var picked = new List<string>();
        picker.SelectionRequested += (_, e) => picked.Add(e.Option.Id);
        picker.IsDropDownOpen = true;

        Press(picker, Avalonia.Input.Key.Escape);

        Assert.False(picker.IsDropDownOpen);
        Assert.Empty(picked);
    }

    /// <summary>The rail narrows the rows; an option belonging to no category shows under every one of them.</summary>
    [Fact]
    public void The_rail_narrows_the_list()
    {
        var anthropic = new PickerCategory { Id = "anthropic", Title = "Anthropic" };
        var picker = new Picker
        {
            Categories = new[] { anthropic, new PickerCategory { Id = "openai", Title = "OpenAI" } },
            Options = new[]
            {
                Option("opus", category: "anthropic"), Option("gpt", category: "openai"), Option("anything"),
            },
        };

        picker.SelectedCategory = anthropic;

        Assert.Equal(["opus", "anything"], picker.Rows.OfType<PickerRow>().Select(row => row.Title));
    }

    /// <summary>A catalogue arriving after the list was built shows up without anybody re-binding.</summary>
    [Fact]
    public void A_list_that_grows_while_it_is_open_is_followed()
    {
        Ui.Run(() =>
        {
            var options = new System.Collections.ObjectModel.ObservableCollection<PickerOption> { Option("a") };
            var picker = new Picker { Options = options };
            var window = new Window { Content = picker };
            try
            {
                window.Show();
                options.Add(Option("b"));

                Assert.Equal(["a", "b"], picker.Rows.OfType<PickerRow>().Select(row => row.Title));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A view rebuilt over the same view model must not leave its old picker subscribed to that
    /// model's list, rebuilding rows nobody can see for as long as the list lives.</summary>
    [Fact]
    public void A_picker_taken_off_screen_stops_following_the_list()
    {
        Ui.Run(() =>
        {
            var options = new System.Collections.ObjectModel.ObservableCollection<PickerOption> { Option("a") };
            var picker = new Picker { Options = options };
            var window = new Window { Content = picker };
            window.Show();
            window.Content = null;
            window.Close();

            options.Add(Option("b"));

            Assert.Equal(["a"], picker.Rows.OfType<PickerRow>().Select(row => row.Title));
        });
    }

    /// <summary>With search off the focus stays on the trigger, a toggle button that takes Enter as a click.
    /// Enter must still pick the highlighted row rather than only closing the list.</summary>
    [Fact]
    public void Enter_on_the_trigger_picks_the_highlighted_row()
    {
        Ui.Run(() =>
        {
            // Fluent's window template is what carries the overlay layer the popup opens into.
            using var theme = new HeadlessTheme();
            var picker = new Picker { Options = new[] { Option("a"), Option("b") } };
            var picked = new List<string>();
            picker.SelectionRequested += (_, e) => picked.Add(e.Option.Id);
            var window = new Window { Content = picker };
            try
            {
                window.Show();
                window.UpdateLayout();
                var trigger = window.GetVisualDescendants().OfType<ToggleButton>()
                    .Single(button => button.Classes.Contains("picker-trigger"));
                picker.IsDropDownOpen = true;

                Press(trigger, Avalonia.Input.Key.Down);
                Press(trigger, Avalonia.Input.Key.Enter);

                Assert.Equal(["b"], picked);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The trigger falls back to the placeholder while the caller has nothing to say.</summary>
    [Fact]
    public void An_empty_text_shows_the_placeholder()
    {
        var picker = new Picker { Placeholder = "Agent" };
        Assert.Equal("Agent", picker.TriggerText);

        picker.Text = "Claude";
        Assert.Equal("Claude", picker.TriggerText);
    }

    /// <summary>The one thing no amount of pure testing catches: a control theme that compiles and then
    /// throws when it is realised, or never resolves its parts at all.</summary>
    [Fact]
    public void It_draws()
    {
        Ui.Run(() =>
        {
            using var theme = new HeadlessTheme();

            var control = new Picker
            {
                Text = "Full access",
                IsSearchEnabled = true,
                Options = new[]
                {
                    new PickerOption
                    {
                        Id = "supervised", Title = "Supervised",
                        Description = "Ask before commands and file changes.",
                    },
                    new PickerOption
                    {
                        Id = "full", Title = "Full access", Badge = "Default", Shortcut = "Ctrl+1",
                        Description = "Allow commands and edits without prompts.",
                    },
                },
                SelectedId = "full",
            };
            var window = new Window { Content = control, Width = 500, Height = 400 };
            try
            {
                window.Show();
                window.UpdateLayout();
                control.IsDropDownOpen = true;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var drawn = window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(text => text.Text).ToList();
                Assert.Contains("Full access", drawn);
                Assert.Contains("Ask before commands and file changes.", drawn);
                Assert.Contains("Default", drawn);
                Assert.Contains("Ctrl+1", drawn);

                // The trigger reports the state of the list, so the two cannot disagree on screen.
                var trigger = window.GetVisualDescendants().OfType<ToggleButton>()
                    .Single(button => button.Classes.Contains("picker-trigger"));
                Assert.True(trigger.IsChecked);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void Search(Picker picker, string text)
    {
        // The search field belongs to the template; what it does when it changes is set this and rebuild,
        // which is the behaviour under test. Reaching it this way keeps these tests window-free.
        typeof(Picker).GetProperty("SearchText",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(picker, text);
        typeof(Picker).GetMethod("Rebuild",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(picker, null);
    }

    /// <summary>Raises the key as a routed event on <paramref name="target"/> — the picker itself, or whatever
    /// inside it has the focus — so the route the application takes is the route under test.</summary>
    private static void Press(Avalonia.Input.InputElement target, Avalonia.Input.Key key) =>
        target.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key,
        });
}

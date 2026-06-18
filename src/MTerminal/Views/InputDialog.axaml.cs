using Avalonia.Controls;
using Avalonia.Input;

namespace MTerminal.Views;

public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
    }

    public InputDialog(string title, string placeholder, IEnumerable<string>? suggestions = null)
    {
        InitializeComponent();

        TitleText.Text = title;
        InputBox.PlaceholderText = placeholder;

        if (suggestions != null)
        {
            var list = suggestions.ToList();
            if (list.Count > 0)
            {
                SuggestionsList.ItemsSource = list;
            }
            else
            {
                SuggestionsList.IsVisible = false;
                SuggestionsLabel.IsVisible = false;
            }
        }
        else
        {
            SuggestionsList.IsVisible = false;
            SuggestionsLabel.IsVisible = false;
        }

        OkButton.Click += (_, _) => Close(InputBox.Text?.Trim());
        CancelButton.Click += (_, _) => Close(null);

        SuggestionsList.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems is { Count: > 0 } && e.AddedItems[0] is string selected)
                InputBox.Text = selected;
        };

        InputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Close(InputBox.Text?.Trim());
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close(null);
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        InputBox.Focus();
    }
}

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using mTiles.Services;
using mTiles.Services.Speech;
using mTiles.ViewModels;

namespace mTiles.Views;

/// <summary>
/// The dictation setup, walked through: a model, a microphone, and a sentence to prove them.
/// </summary>
/// <remarks>
/// One window for both occasions — the first run, where none of it is set up, and Settings → Speech,
/// where it is how somebody starts over. It replaced a single-screen prompt that asked which model to
/// download and nothing else, which could not answer the only question that matters: does this work.
/// </remarks>
public partial class SpeechSetupWizard : Window
{
    private SpeechSetupViewModel? _model;

    public SpeechSetupWizard()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    /// <summary>
    /// Runs the wizard over <paramref name="owner"/> and returns when it is closed.
    /// </summary>
    /// <remarks>
    /// The view model is disposed here rather than by whoever opened it: it holds a subscription to the
    /// dictation service, which outlives this window, and it may be holding the microphone — closing the
    /// window mid-sentence has to give both back.
    /// </remarks>
    public static async Task ShowAsync(Window owner, DictationService dictation, SettingsService settings)
    {
        var model = new SpeechSetupViewModel(dictation, settings);
        var window = new SpeechSetupWizard { DataContext = model, _model = model };

        model.CloseRequested += window.Close;
        try
        {
            await window.ShowDialog(owner);
        }
        finally
        {
            model.CloseRequested -= window.Close;
            model.Dispose();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // Also here, because a window can be closed by the title bar without going through ShowAsync's
        // finally in any way it could rely on. Disposing twice is harmless; leaving a recording running
        // is not.
        _model?.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

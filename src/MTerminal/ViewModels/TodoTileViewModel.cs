using MTerminal.Services;

namespace MTerminal.ViewModels;

public class TodoTileViewModel(string filePath, SettingsService? settingsService = null)
    : MarkdownTileViewModel(filePath, settingsService);

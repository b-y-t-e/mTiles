using MTerminal.Services;

namespace MTerminal.ViewModels;

public class NoteTileViewModel(string filePath, SettingsService? settingsService = null)
    : MarkdownTileViewModel(filePath, settingsService);

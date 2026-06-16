# MTerminal

Multiplatformowy terminal manager — .NET 10 + Avalonia 12.

## Budowanie i uruchamianie

```bash
dotnet build
dotnet run --project src/MTerminal
```

## Struktura

- `src/MTerminal/` — jedyny projekt w solucji
- `Models/` — DTO i modele danych (Project, PaneNode, AppSettings, ShellProfile)
- `ViewModels/` — MVVM z CommunityToolkit.Mvvm (source generators)
- `Views/` — Avalonia AXAML + code-behind
- `Services/` — persystencja JSON (projekty, workspace layout, ustawienia)

## Kluczowe biblioteki

- **Iciclecreek.Avalonia.Terminal** — terminal z wbudowanym PTY (Porta.Pty). Ważne: `BeginReparent()`/`EndReparent()` zapobiega zabijaniu procesu przy przenoszeniu kontrolki w visual tree.
- **AvaloniaEdit** — edytor tekstu. Wymaga `Focusable = true` (domyślnie false).

## Architektura split panes

Rekurencyjne drzewo binarne: `LeafPaneNodeViewModel` (terminal/edytor) lub `SplitPaneNodeViewModel` (H/V + dwoje dzieci). `PaneNodeView` zarządza widokami ręcznie (nie DataTemplate) żeby zachować live terminale przy przebudowie drzewa.

## Persystencja

- `%APPDATA%/MTerminal/` (Windows) lub `~/.config/MTerminal/` (Linux)
- `settings.json`, `projects.json`, `workspaces/{id}.json`
- Auto-save z debounce

## Konwencje

- MVVM: ViewModele w `ViewModels/`, widoki w `Views/`
- Brak DI container — ręczne wstrzykiwanie w `App.axaml.cs`
- Commituj po polsku

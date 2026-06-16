# MTerminal

Multiplatformowy terminal manager — .NET 10 + Avalonia 12.

## Budowanie i uruchamianie

```bash
dotnet build
dotnet run --project src/MTerminal
```

## Struktura

- `src/MTerminal/` — jedyny projekt w solucji
- `Models/` — DTO i modele danych (Workspace, PaneNode, AppSettings, ShellProfile, TerminalTheme)
- `ViewModels/` — MVVM z CommunityToolkit.Mvvm (source generators)
- `Views/` — Avalonia AXAML + code-behind
- `Services/` — persystencja JSON, detekcja shelli (ShellDetector), JsonDefaults

## Kluczowe biblioteki

- **Iciclecreek.Avalonia.Terminal** — terminal z wbudowanym PTY (Porta.Pty).
  - `BeginReparent()`/`EndReparent()` zapobiega zabijaniu procesu przy przenoszeniu w visual tree
  - `Process = string.Empty` blokuje auto-launch domyślnego shella
  - Class handlery (`OnKeyDown`) ignorują `e.Handled` — nie da się ich zablokować zwykłymi handlerami
- **AvaloniaEdit** — edytor tekstu. Wymaga `StyleInclude` w App.axaml. Sync tekstu przez `Document.Changed`.

## Architektura split panes

Rekurencyjne drzewo binarne: `LeafPaneNodeViewModel` (terminal/edytor) lub `SplitPaneNodeViewModel` (H/V + dwoje dzieci). `PaneNodeView` zarządza widokami ręcznie (nie DataTemplate) i wywołuje `SuspendTerminals()`/`ResumeTerminals()` wokół Rebuild żeby zachować live terminale.

## Obsługa klawiszy terminala

`TerminalKeyHandler` (osobna klasa, SRP) obsługuje:
- **Ctrl+V** — paste via `PasteAsync()`
- **Ctrl+C** — copy zaznaczonego tekstu (pre-captured w `PointerReleased`, bo TerminalView czyści selekcję przy każdym keydown)
- **Alt+key** — wysyła `ESC+char` bezpośrednio do PTY stream (fix brakującej obsługi Alt w TerminalView)

Refleksja na `_ptyConnection.WriterStream` jest konieczna bo TerminalView nie eksponuje PTY stream publicznie.

## Persystencja

- `%APPDATA%/MTerminal/` (Windows) lub `~/.config/MTerminal/` (Linux)
- `settings.json` — ustawienia (fonty, theme terminala, default shell, stan okna)
- `workspaces.json` — lista workspace'ów (id, nazwa, ścieżka)
- `workspaces/{id}.json` — layout paneli per workspace (shell name, pane name)
- Auto-save z debounce

## Konwencje

- **Workspace** (nie "project") — katalog roboczy z panelami terminali/edytorów
- ViewModele w `ViewModels/`, widoki w `Views/`
- Brak DI container — ręczne wstrzykiwanie w `App.axaml.cs`

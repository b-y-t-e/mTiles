# MTerminal

Multiplatformowy terminal manager — .NET 10 + Avalonia 12.

## Budowanie i uruchamianie

```bash
dotnet build
dotnet run --project src/MTerminal
```

## Struktura

- `src/MTerminal/` — jedyny projekt w solucji
- `Models/` — DTO i modele danych (Workspace, TileNode, AppSettings, ShellProfile, TerminalTheme)
- `ViewModels/` — MVVM z CommunityToolkit.Mvvm (source generators)
- `Views/` — Avalonia AXAML + code-behind
- `Styles/` — design tokens (`AppTheme.axaml`) i globalne style kontrolek (`Controls.axaml`). Kolory UI wyłącznie przez `DynamicResource`, terminal ANSI colors osobno w `TerminalTheme`
- `Services/` — persystencja JSON, detekcja shelli (ShellDetector), ThemeBridge, JsonDefaults

## Kluczowe biblioteki

- **Iciclecreek.Avalonia.Terminal** — terminal z wbudowanym PTY (Porta.Pty).
  - `BeginReparent()`/`EndReparent()` zapobiega zabijaniu procesu przy przenoszeniu w visual tree
  - `Process = string.Empty` blokuje auto-launch domyślnego shella
  - Class handlery (`OnKeyDown`) ignorują `e.Handled` — nie da się ich zablokować zwykłymi handlerami
- **AvaloniaEdit** — edytor tekstu. Wymaga `StyleInclude` w App.axaml. Sync tekstu przez `Document.Changed`.
- **Material.Icons.Avalonia** — ikony Material Design. Wymaga `<MaterialIconStyles />` w `App.axaml` Styles. Użycie: `<mi:MaterialIcon Kind="Close" />`.

## Architektura split tiles

Rekurencyjne drzewo binarne: `LeafTileNodeViewModel` (terminal/edytor) lub `SplitTileNodeViewModel` (H/V + dwoje dzieci). `TileNodeView` zarządza widokami ręcznie (nie DataTemplate) i wywołuje `SuspendTerminals()`/`ResumeTerminals()` wokół Rebuild żeby zachować live terminale.

`LeafTileNodeViewModel.IsActive` — globalny static event `ActiveTileChanged` gwarantuje, że tylko jeden tile jest aktywny. `LeafTileView` reaguje na `IsActive` — kolorowy pasek (`ActiveStrip`, 2px) na górze toolbara + jaśniejsze tło (`BgElevated`).

## Obsługa klawiszy terminala

`TerminalKeyHandler` (osobna klasa, SRP) obsługuje:
- **Ctrl+V** — paste via `PasteAsync()`
- **Ctrl+C** — copy zaznaczonego tekstu (pre-captured w `PointerReleased`, bo TerminalView czyści selekcję przy każdym keydown)
- **Alt+key** — wysyła `ESC+char` bezpośrednio do PTY stream (fix brakującej obsługi Alt w TerminalView)

Refleksja na `_ptyConnection.WriterStream` jest konieczna bo TerminalView nie eksponuje PTY stream publicznie.

## Alt-buffer cleanup (TUI apps)

`AttachAltBufferCleanup` w `TerminalTileView` resetuje mouse tracking (refleksja na `XTerm.Terminal._mouseTracker`) gdy `IsAlternateBuffer` zmienia się z `true` na `false`. Bez tego po wyjściu z opencode/vim terminal jest zalewany sekwencjami SGR mouse.

## ThemeBridge — synchronizacja UI z motywem terminala

`ThemeBridge.Apply(TerminalTheme)` w `App.axaml.cs` dynamicznie wyprowadza kolory UI (tła, bordery, tekst, akcenty) z aktywnego motywu terminala. Wywoływany na starcie i przy każdym `SettingsChanged`. Dzięki `DynamicResource` cały UI reaguje natychmiast na zmianę motywu.

## Restart shell

`RestartTerminal` w `LeafTileNodeViewModel` — kill + relaunch PTY. Dostępny przez ikonę Restart w headerze tile'a i Ctrl+Shift+R. Workaround na hang ConPTY po Ctrl+C w TUI apps (znany bug opencode na Windows).

## Scrollbar Fluent theme fix

`AppTheme.axaml` nadpisuje `VerticalSmallScrollThumbScaleTransform` / `HorizontalSmallScrollThumbScaleTransform` na `none`. Bez tego Fluent theme skaluje thumb do 12.5% na maszynach z domyślnym Windows "auto-hide scrollbars".

## Persystencja

- `%APPDATA%/MTerminal/` (Windows) lub `~/.config/MTerminal/` (Linux)
- `settings.json` — ustawienia (fonty, theme terminala, default shell, stan okna)
- `workspaces.json` — lista workspace'ów (id, nazwa, ścieżka)
- `workspaces/{id}.json` — layout tile'ów per workspace (shell name, tile name)
- Auto-save z debounce

## Konwencje

- **Workspace** (nie "project") — katalog roboczy z tile'ami terminali/edytorów. PPM na workspace → context menu (Show in Explorer, Remove).
- **Tile** (nie "pane"/"panel") — pojedynczy kafelek w workspace (terminal lub notatka), dzielony w drzewo binarne
- **Note** (nie "editor") — tile z edytorem tekstu (AvaloniaEdit), TileContentType.Note
- ViewModele w `ViewModels/`, widoki w `Views/`
- Brak DI container — ręczne wstrzykiwanie w `App.axaml.cs`

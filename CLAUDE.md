# MTerminal

Multiplatformowy terminal manager — .NET 10 + Avalonia 12.

## Budowanie i uruchamianie

```bash
dotnet build
dotnet run --project src/MTerminal
```

## Struktura

- `src/MTerminal/` — jedyny projekt w solucji
- `Models/` — DTO i modele danych (Workspace, TileNode, AppSettings, AppDefaults, ShellProfile, UserShellProfile, TerminalTheme, GitFileChange, CommitLogEntry)
- `ViewModels/` — MVVM z CommunityToolkit.Mvvm (source generators)
- `Views/` — Avalonia AXAML + code-behind
- `Styles/` — design tokens (`AppTheme.axaml`) i globalne style kontrolek (`Controls.axaml`). Kolory UI wyłącznie przez `DynamicResource`, terminal ANSI colors osobno w `TerminalTheme`
- `Services/` — persystencja JSON (PersistenceService, SettingsService, WorkspaceService), detekcja shelli (ShellDetector), ThemeBridge, JsonDefaults, AppPaths, GitService/GitCommandRunner/GitDirectoryWatcher, DiffFormatter, FileHelper, TileFactory, TileTreeSerializer, UpdateService, CrashHandler, FileLogWriter, LogTraceListener
- `Views/PtyWriter.cs` — statyczny helper do zapisu do PTY przez refleksję (`TerminalView._ptyConnection.WriterStream`). Używany przez `TerminalKeyHandler` i startup script w `TerminalTileView`

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

Refleksja na PTY stream wyekstrahowana do `PtyWriter` (statyczny helper, wspólny dla key handler i startup script).

## Alt-buffer cleanup (TUI apps)

`AttachAltBufferCleanup` w `TerminalTileView` resetuje mouse tracking (refleksja na `XTerm.Terminal._mouseTracker`) gdy `IsAlternateBuffer` zmienia się z `true` na `false`. Bez tego po wyjściu z opencode/vim terminal jest zalewany sekwencjami SGR mouse.

## ThemeBridge — synchronizacja UI z motywem terminala

`ThemeBridge.Apply(TerminalTheme)` w `App.axaml.cs` dynamicznie wyprowadza kolory UI (tła, bordery, tekst, akcenty) z aktywnego motywu terminala. Wywoływany na starcie i przy każdym `SettingsChanged`. Dzięki `DynamicResource` cały UI reaguje natychmiast na zmianę motywu.

## Shell Profiles

Użytkownik definiuje profile shella w Settings → zakładka Profiles. Każdy `UserShellProfile` ma: `Id` (GUID), `Name`, `ShellName` (referencja do wykrytego shella), `StartupScript` (komendy wysyłane do PTY po starcie).

Flow tworzenia terminala z profilem:
1. Empty tile → klik Terminal → jeśli są profile, pojawia się ProfileChooser (Back / Default / przyciski profili)
2. Wybór profilu → `TileFactory.CreateContent(..., UserShellProfile)` → `ShellDetector.ResolveFromUserProfile()` → `TerminalTileViewModel` z shellem + startup scriptem
3. Po `LaunchProcess` w `TerminalTileView`, `ShellReady` event triggeruje wysłanie startup scriptu linia po linii przez `PtyWriter`

Persystencja profilu w layout: `TileNode.UserProfileId` → przy deserializacji `TileFactory.CreateTerminalFromDto` szuka profilu po Id w `AppSettings.ShellProfiles`. Jeśli profil usunięty — graceful fallback na `ShellName`.

## Settings UI

Dialog Settings jako modal overlay z responsywnym rozmiarem (50% szerokości / 80% wysokości okna, min 420×400). Dwie zakładki:
- **General** — Default Shell, Appearance (theme, color theme, font), Terminal (font)
- **Profiles** — CRUD profili shella (lista + inline edit z akcentowym borderem)

`SettingsViewModel.SelectedTab` steruje widocznością zakładek. Style tab-buttons: `settings-tab` / `settings-tab-active` w `Controls.axaml`.

## Restart shell

`RestartTerminal` w `LeafTileNodeViewModel` — kill + relaunch PTY. Dostępny przez ikonę Restart w headerze tile'a i Ctrl+Shift+R. Workaround na hang ConPTY po Ctrl+C w TUI apps (znany bug opencode na Windows).

## Scrollbar Fluent theme fix

`AppTheme.axaml` nadpisuje `VerticalSmallScrollThumbScaleTransform` / `HorizontalSmallScrollThumbScaleTransform` na `none`. Bez tego Fluent theme skaluje thumb do 12.5% na maszynach z domyślnym Windows "auto-hide scrollbars".

## Crash handling i logowanie

`CrashHandler` przechwytuje wyjątki z trzech źródeł: `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, `Dispatcher.UIThread.UnhandledException`. Inicjalizowany w `Program.Main()` przed startem Avalonia.

`FileLogWriter` zapisuje logi do `%APPDATA%/MTerminal/logs/mterminal-YYYY-MM-DD.log` z automatycznym czyszczeniem plików starszych niż 7 dni. `LogTraceListener` przekierowuje `Trace` do plików logów.

## Persystencja

- `%APPDATA%/MTerminal/` (Windows) lub `~/.config/MTerminal/` (Linux)
- `settings.json` — ustawienia (fonty, theme terminala, default shell, shell profiles, stan okna)
- `workspaces.json` — lista workspace'ów (id, nazwa, ścieżka)
- `workspaces/{id}.json` — layout tile'ów per workspace (shell name, user profile id, tile name)
- `logs/` — logi aplikacji (dzienne pliki, retencja 7 dni)
- Auto-save z debounce

## Konwencje

- **Workspace** (nie "project") — katalog roboczy z tile'ami terminali/edytorów. PPM na workspace → context menu (Show in Explorer, Remove).
- **Tile** (nie "pane"/"panel") — pojedynczy kafelek w workspace (terminal, notatka lub todo), dzielony w drzewo binarne
- **Note** (nie "editor") — tile z edytorem tekstu (AvaloniaEdit), TileContentType.Note
- **Todo** — tile z listą zadań, TileContentType.Todo
- ViewModele w `ViewModels/`, widoki w `Views/`
- **Git** — tile z podglądem zmian (diff, commit, stash, context menu, discard), TileContentType.Git
- Brak DI container — ręczne wstrzykiwanie w `App.axaml.cs`, `TileFactory` jako fabryka contentu tile'ów
- **ConfirmAction pattern** — destructive actions (discard, remove workspace) używają `Func<string, Task<bool>>? ConfirmAction` w ViewModel, podpięty z View jako `MessageBox.Avalonia` dialog (YesNo)

## Git tile — szczegóły

`GitDirectoryWatcher` obserwuje zarówno `.git/` jak i cały katalog roboczy (worktree). Lista ignorowanych katalogów pobierana z `git ls-files --ignored` i aktualizowana przy każdym refresh. Handlery `Error` na watcherach logują przepełnienie bufora i triggerują refresh.

`ReconcileChanges` w `GitTileViewModel` zachowuje stan checkboxów (`IsChecked`) między refresh'ami na podstawie klucza (FilePath + Status + mtime). Dwupoziomowy cache (currentState + previousState) chroni przed utratą stanu przy "migających" plikach. Przy pierwszym ładowaniu checkboxy = false, przy kolejnych refresh'ach nowe/zmienione pliki = true.

Context menu (PPM) na liście plików: Show in Explorer, Open in default program, Copy filename/folder/filepath, Discard changes (z dialogiem potwierdzenia). Multi-select: PPM pokazuje tylko Discard z liczbą plików. Space toggle'uje checkboxy zaznaczonych plików.

# MTerminal

Multiplatformowy terminal manager z systemem kafelkowych paneli. Windows, Linux, macOS.

## Funkcje

- **Workspaces** — każdy workspace powiązany z katalogiem, przełączanie jednym kliknięciem, wyświetlanie aktualnego brancha git
- **Split panes** — dzielenie paneli horyzontalnie/wertykalnie w dowolnych kombinacjach
- **Terminale** — Git Bash, PowerShell, CMD (auto-detekcja), wybór domyślnego w ustawieniach
- **Shell Profiles** — nazwane profile z wyborem shella i skryptem startowym, chooser profilu przy tworzeniu terminala
- **Git tile** — podgląd zmian w stylu GitHub Desktop: diff (unified + side-by-side), commit, stash, push/fetch, tag management, undo last commit, commit message suggestions, historia commitów z tagami i oznaczeniem niepushowanych, context menu (Show in Explorer, Open, Copy path/hash, Discard, Add tag), auto-refresh przy zmianach w worktree
- **Motywy terminala** — Default Dark, Dracula, Nord, Monokai, Solarized Dark, Catppuccin Mocha
- **Edytor notatek** — AvaloniaEdit z numeracją linii, auto-zapis do `.mterminal/notes/`
- **Lista Todo** — inline-editable checklist, Enter dodaje element, checkbox przesuwa na dół, auto-zapis do `.mterminal/todos/`
- **Skróty klawiszowe** — Ctrl+C/V (copy/paste), Alt+key (ESC sequences), Ctrl+Shift+R (restart shell)
- **Context menu workspace** — PPM → otwórz folder w eksploratorze (Windows/macOS/Linux), usuń workspace
- **Rename paneli** — double-click na nazwę, auto-numeracja (Terminal #1, Note #1, Todo #1)
- **Resizable panel** — workspace panel z regulowaną szerokością
- **Crash logging** — logowanie wyjątków i trace do plików dziennych z automatyczną retencją
- **AI Tools** — autodetekcja 18+ CLI AI coding tools (Claude Code, Cursor, Copilot CLI...), test wersji, custom tools
- **Persystencja** — layout, workspaces, ustawienia, stan okna, profil shella

## Wymagania

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Uruchomienie

```bash
git clone <repo-url>
cd mterminal
dotnet run --project src/MTerminal
```

## Tech stack

- .NET 10 + Avalonia 12
- Iciclecreek.Avalonia.Terminal (PTY)
- AvaloniaEdit (edytor tekstu)
- CommunityToolkit.Mvvm
- MessageBox.Avalonia
- Material.Icons.Avalonia

## Licencja

MIT

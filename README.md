# MTerminal

Multiplatformowy terminal manager z systemem kafelkowych paneli. Windows, Linux (Android w przyszłości).

## Funkcje

- **Workspaces** — każdy workspace powiązany z katalogiem, przełączanie jednym kliknięciem
- **Split panes** — dzielenie paneli horyzontalnie/wertykalnie w dowolnych kombinacjach
- **Terminale** — PowerShell, Git Bash, CMD (auto-detekcja), wybór domyślnego w ustawieniach
- **Motywy terminala** — Default Dark, Dracula, Nord, Monokai, Solarized Dark, Catppuccin Mocha
- **Edytor notatek** — AvaloniaEdit z numeracją linii, auto-zapis do `.mterminal/notes/`
- **Skróty klawiszowe** — Ctrl+C/V (copy/paste), Alt+key (ESC sequences dla CLI tools)
- **Rename paneli** — double-click na nazwę, auto-numeracja (Terminal #1, Note #1)
- **Resizable panel** — workspace panel z regulowaną szerokością
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

## Licencja

MIT

# MTerminal

Multiplatformowy terminal manager z systemem kafelkowych paneli. Windows, Linux (Android w przyszłości).

## Funkcje

- **Projekty** — każdy projekt powiązany z katalogiem, przełączanie jednym kliknięciem
- **Split panes** — dzielenie paneli horyzontalnie/wertykalnie w dowolnych kombinacjach
- **Terminale** — PowerShell, Git Bash, CMD (auto-detekcja), wybór domyślnego w ustawieniach
- **Edytor notatek** — AvaloniaEdit z numeracją linii, auto-zapis do `.mterminal/notes/` w katalogu projektu
- **Persystencja** — layout, projekty i ustawienia zapisywane automatycznie

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

## Licencja

MIT

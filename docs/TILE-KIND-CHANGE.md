# Zmiana rodzaju kafla w miejscu (`… → Change type`)

**Werdykt: możliwe, wykonalne i bezpieczne — pod trzema warunkami**, które ten plik rozpisuje: nic nie
ginie zanim użytkownik nie potwierdzi; kafle z własnym krokiem konfiguracji (terminal, agent) wybierają
go **przed** zniszczeniem starej treści; a to, co zostaje na dysku, jest powiedziane wprost.

Data: 2026-09-04. Stan kodu: `ViewModels/LeafTileNodeViewModel.cs`, `Services/Tiles/`,
`Views/LeafTileView.axaml(.cs)`, `Models/TileNode.cs`.

---

## 1. Dlaczego to jest wykonalne małym kosztem

Architektura już to niesie — nie trzeba niczego przebudowywać:

| Co jest potrzebne | Co już istnieje |
|---|---|
| Zbudowanie dowolnego rodzaju z niczego | `ITileKind.Create(context, state)` — **jedna** droga dla chooseru, dla zapisanego layoutu i dla nowego kafla |
| Lista rodzajów do wyboru | `TileCatalog.Entries` — kolejność rejestracji jest kolejnością w chooserze |
| Przerysowanie kafla po zmianie | `LeafTileView.OnLeafPropertyChanged` reaguje na `Content` **i** `KindId`, a widok rozwiązuje się słownikiem po `KindId` (`SetContent`), nigdy `switch`-em po typie |
| Przepięcie obserwacji (busy light, akcje nagłówka, tło) | `OnContentChanged` → `WatchContent` + `RaiseActionsChanged` + `OnPropertyChanged(IsBusy/CanMaximize)` |
| Zapis nowego rodzaju | `TileTreeSerializer.Serialize` czyta `leaf.KindId` i `kind.Save(content)`; pola legacy w `TileNode` są **bramkowane rodzajem** (`Echo(kind, key)`), więc po konwersji nie zostaje ani `shellName` w notatce, ani `filePath` w terminalu |
| Krok konfiguracji rodzaju | `ITileKind.SetupOptions(context)` + istniejący `SetupChooserScroll` i `SelectSetupOptionCommand` |

Brakuje **jednej** rzeczy: `Adopt` nadaje treść, ale nie usuwa poprzedniej — bo jego jedynym wywołującym
jest dziś pusty kafel. Konwersja to `Adopt` poprzedzone porządnym demontażem.

## 2. Co dokładnie ginie, a co zostaje

To jest cała analiza bezpieczeństwa; z niej wynika treść pytania zadawanego użytkownikowi.

| Rodzaj (obecny) | Co ginie bezpowrotnie | Co zostaje |
|---|---|---|
| `terminal` | sesja PTY i całe drzewo procesów potomnych (`TerminalTileViewModel.Dispose` → `tc.Dispose()`), scrollback | — |
| `agent` | to samo + bieżący proces CLI | rozmowa u agenta (id sesji = `TileId`), `sessions/opencode/ses_<tileId>.json`, katalog sign-inu |
| `goal` | nic — `Dispose` **pauzuje** bieg i zapisuje stan | `.mtiles/goals/<id>.json` z całym transkryptem; baseline w `refs/mtiles/` |
| `note` / `todo` | nic | plik w `.mtiles/notes/` / `todos/` — osierocony, ale w całości na dysku |
| `git` | nic (subskrypcja `WorkspaceGitWatcher` odchodzi z ostatnim subskrybentem) | repozytorium |
| `database` | nic; kafel wyrejestrowuje workspace, więc serwer HTTP przestaje wystawiać te bazy | `.mtiles/databases.json` |
| `usage` | nic (`Save` zwraca `null`, kafel nic nie trzyma) | — |

**Wniosek:** jedyna nieodwracalna strata to żywy shell lub agent. Reszta to najwyżej plik, do którego
kafel przestaje wskazywać. Ta różnica musi być w treści pytania — nie jedno zdanie dla wszystkiego.

## 3. Wybrany kształt

**`…` → `Change type ▸ <lista rodzajów>`**, submenu budowane z `TileCatalog.Entries` z pominięciem
rodzaju bieżącego (ta sama ikona, ten sam akcent i ta sama kolejność co karty chooseru — obie listy
czytają rejestr, więc nie mogą się rozjechać).

Przepływ, i **kolejność jest tu całą treścią decyzji**:

1. **Wybór rodzaju docelowego** — nic się jeszcze nie dzieje.
2. **Krok konfiguracji, jeśli rodzaj go ma** (`SetupOptions.Count > 0`: terminal → powłoka, agent →
   instancja). Rysowany zamiast treści kafla, ze `Wstecz`/`Anuluj`, **stara treść nadal żyje** —
   terminal dalej pracuje, kiedy wybierasz powłokę dla jego następcy.
3. **Potwierdzenie** — jedno zdanie mówiące, co konkretnie umiera (tabela §2). Dopiero tutaj, bo
   dopiero tutaj jest komplet: co znika i co powstaje.
4. **Konwersja** — dopiero teraz cokolwiek jest niszczone.

Odrzucone warianty:

- **„Wyczyść kafel do pustego, niech zadziała istniejący chooser"** — najmniej kodu i najgorsza
  własność: treść ginie w kroku 1, a użytkownik wybiera rodzaj już po stracie. `Anuluj` nie ma czego
  przywrócić.
- **Konwersja na ustawieniach domyślnych** (domyślna powłoka / pierwsza instancja agenta) — agent nie ma
  sensownej wartości domyślnej, a ciche wybranie konta AI to dokładnie ta klasa decyzji, której to
  repozytorium nie podejmuje za użytkownika (patrz reguła o `AccountChoice` w `CLAUDE.md`).
- **Nowy kafel obok + zamknięcie starego** — zmienia układ, gubi pozycję w drzewie i stan pełnego
  ekranu. Prośba dotyczy zamiany, nie przeniesienia.

## 4. Zmiany w kodzie

### 4.1 `ViewModels/LeafTileNodeViewModel.cs`

```csharp
/// <summary>Rodzaje, na jakie ten kafel może się zamienić — wszystkie zarejestrowane poza bieżącym.</summary>
public IReadOnlyList<TileKindChoice> ChangeKindOptions { get; private set; } = [];
public bool CanChangeKind => ChangeKindOptions.Count > 0;
public void RefreshChangeKindOptions();   // budowane przy otwarciu menu, jak RefreshAgentInstances

[RelayCommand] private Task BeginChangeKindAsync(string? kindId);   // krok 1 → 2 albo 1 → 3
```

- `TileKindChoice` — record `(string KindId, string Label, string IconId, string AccentKey, ICommand Command)`,
  wzorowany na `AgentInstanceChoice`: menu wiąże się do view-modelu kafla, nie do jego treści.
- **`_pendingKindId`** obok istniejącego `_setupKindId`: mówi, że rysowany krok konfiguracji jest
  konwersją, a nie wypełnianiem pustego kafla. `SelectSetupOption` rozgałęzia się na `Adopt`
  (pusty kafel) albo `ConvertToAsync` (konwersja) — **jedna** różnica, nie druga ścieżka.
- Jedyne miejsce, które demontuje treść bez zamykania kafla:

```csharp
private async Task ConvertToAsync(string kindId, JsonObject? state)
{
    if (_catalog?.Kind(kindId) is not { } kind || _context is not { } context) return;
    if (ConfirmAction is { } ask && !await ask(TileConversion.Warning(KindId, kind.DisplayName))) return;

    // 1. Dyktowanie — ten kafel może być właścicielem nagrania. Pytamy serwis, kto nagrywa, a nie
    //    własnej flagi IsDictating: ta jest ustawiana z callbacku dispatchera, więc między startem a
    //    callbackiem czyta się jeszcze jako false. Ta sama reguła co w Dispose.
    if (Dictation is { } d && ReferenceEquals(d.Owner, this)) d.Cancel();

    // 2. Nowa treść na miejsce, dopiero potem demontaż starej.
    var old = Content;
    Content = kind.Create(context, state);   // OnContentChanged przepina obserwacje
    old?.Dispose();

    // 3. Pełny ekran: nowy rodzaj może nie być IMaximizableTile, a splity zostałyby zsolowane na
    //    kaflu, który nie umie ich cofnąć — to jest ta sama awaria, przed którą chroni Forget w Dispose.
    //    Po podmianie Content, bo CanMaximize pyta o treść, która jest teraz.
    if (!CanMaximize) MaximizeScope?.Forget(this);

    KindId = kindId;
    TileName = _nameFactory?.Invoke(kindId) ?? kind.DisplayName;
    (Content as IFileContent)?.RenameFile(TileName);
    NotifyLayoutChanged();
    RequestFocus();
}
```

**Kolejność `Content = …` przed `old.Dispose()`** jest celowa: nowa treść musi być na miejscu, zanim
stara zacznie odpalać zdarzenia demontażu — inaczej `IsBusy`, akcje nagłówka i tło zostają na moment bez
właściciela, a widok dostaje `null`, czyści `ContentHost` i zaraz odbudowuje go z powrotem (mignięcie).

### 4.2 `Services/Tiles/TileConversion.cs` (nowy, czysty)

Jedna reguła: zdanie ostrzeżenia dla pary (rodzaj bieżący → nazwa rodzaju docelowego) plus
`bool DestroysWork(string kindId)`. Czyste, więc **argumentowane testem tabelarycznym** — to jest osąd
o tym, co użytkownik traci, a nie mechanika:

- `terminal` / `agent` → „Powłoka i wszystko, co w niej działa, zostaną zakończone."
- `agent` dodatkowo → „Rozmowa zostaje u agenta; kafel przestanie ją otwierać."
- `note` / `todo` → „Plik zostaje w `.mtiles/…`; kafel przestanie na niego wskazywać."
- `goal` → „Bieg zostanie wstrzymany, a jego zapis zostaje w `.mtiles/goals/`."
- `git` / `database` / `usage` → krótkie „Nic z tego kafla nie zostanie utracone."

Nieznany rodzaj (zarejestrowany przez kod, o którym ten plik nie wie) dostaje zdanie ogólne — nie
wyjątek: kafel z rodzaju spoza tej listy ma się dać zamienić, tylko bez obietnicy, co po nim zostanie.

`ConfirmAction` **niepodpięte przepuszcza akcję** — to jest konwencja tej klasy (`ResetTileIdAsync`),
w odróżnieniu od okna Settings, gdzie odpowiada „nie". Zapisane tutaj wprost, żeby następny czytelnik
nie musiał tego wyprowadzać z dwóch różnych miejsc.

### 4.3 `Views/LeafTileView.axaml`

Nowy `MenuItem Header="Change type"` z `ItemsSource="{Binding ChangeKindOptions}"` i
`IsVisible="{Binding CanChangeKind}"`, **pod separatorem** — razem z `Full screen` i splitami, bo to
rzecz robiona kaflowi, a nie jego zawartości. Styl pozycji jak w `Run as`
(`Style Selector="MenuItem > MenuItem"`, a **nie** `ItemContainerTheme`: theme musi opierać się o motyw
kontrolki, który jest statycznym zasobem, i widok rzucał wyjątek wszędzie tam, gdzie budowano go bez
motywu). `OnOverflowOpening` woła `RefreshChangeKindOptions()` obok istniejącego
`RefreshAgentInstances()`.

Świadomie **tylko w menu, bez przycisku w pasku**: to jest akcja robiona raz na kafel, a pasek nagłówka
już oddaje przyciski przy wąskim kaflu (`ApplyHeaderWidth`).

### 4.4 `Views/LeafTileView.axaml.cs`

`UpdateChooserVisibility` przestaje wychodzić na `KindId.Length > 0` — warunkiem staje się
`IsChoosingSetup`, a `ContentHost.IsVisible` jego zaprzeczeniem. Dwie linie; to jedyne miejsce, w
którym widok w ogóle dowiaduje się, że konwersja istnieje.

## 5. Decyzje, które trzeba podjąć świadomie

1. **`TileId` zostaje ten sam.** Kafel jest tym samym kaflem w tym samym miejscu drzewa; nowy id zerwałby
   powiązanie z pełnym ekranem i aktywnością. Konsekwencja warta komentarza w kodzie: konwersja
   `agent → note → agent` **wraca do tej samej rozmowy**, bo id sesji jest id kafla. To jest cecha, nie
   wypadek — ale musi być napisana, bo z drugiej strony wygląda jak wyciek stanu.
2. **Nazwa jest generowana od nowa.** `brave-otter` po zamianie na notatkę nazywa coś, czego już nie ma.
   Kosztem jest nazwa nadana ręcznie — nie ma dziś flagi „użytkownik zmienił nazwę"; jeśli ma przetrwać,
   trzeba ją najpierw dodać, i to jest osobna zmiana.
3. **Stan starego rodzaju nie jest pamiętany.** Powrót do poprzedniego rodzaju daje *nowy* pusty kafel
   tego rodzaju (plik notatki zostaje na dysku, ale kafel dostanie inny). Zapisywanie „poprzedniego
   stanu" w layoucie znaczyłoby rozszerzenie formatu, który ma twarde reguły wstecznej zgodności — nie
   warte tego, co kupuje.
4. **Nie blokujemy konwersji kafla, który pracuje.** Blokada byłaby kłamstwem — powłoka przy prompcie
   też „nic nie robi" — więc zamiast niej zdanie potwierdzenia dla `terminal`/`agent` mówi wprost, że
   proces zostanie zakończony.

## 6. Testy

| Test | Co pilnuje |
|---|---|
| `TileConversionTests` (tabelaryczny) | zdanie ostrzeżenia dla każdego zarejestrowanego rodzaju; żaden rodzaj bez zdania |
| `Converting_disposes_the_old_content_exactly_once` | stara treść dostaje `Dispose`, nowa nie |
| `Converting_a_terminal_ends_its_session` | `FakePty` — dziecko zabite, nie osierocone |
| `A_refused_confirmation_changes_nothing` | `ConfirmAction` → `false`: ten sam `Content`, ten sam `KindId`, brak zapisu layoutu |
| `Converting_keeps_the_tile_in_place` | ten sam `TileId`, ta sama pozycja w drzewie, aktywność bez zmian |
| `A_maximized_tile_converted_to_a_kind_that_cannot_be_maximized_is_restored` | `TileMaximizeScope` nie zostaje zsolowany na kaflu, który nie umie wrócić |
| `Converting_writes_only_the_new_kinds_fields` | `TileTreeSerializer` + `TileNode`: brak `shellName` w notatce i `filePath` w terminalu |
| `The_setup_step_of_a_conversion_can_be_cancelled` | po `Anuluj` treść i rodzaj bez zmian, powłoka dalej żyje |
| `Layout_round_trip_after_a_conversion` | zapis → odczyt → ten sam kafel |
| `Converting_a_dictating_tile_cancels_the_recording` | nagranie nie zostaje wpięte w treść, której już nie ma |

## 7. Kolejność prac

1. `TileConversion` + test tabelaryczny (czysta reguła, zero UI).
2. `ConvertToAsync` + `_pendingKindId` w `LeafTileNodeViewModel` + testy demontażu.
3. Krok konfiguracji dla konwersji (rozgałęzienie w `SelectSetupOption`, `UpdateChooserVisibility`).
4. Submenu w `…` + `RefreshChangeKindOptions` w `OnOverflowOpening`.
5. Akapit w `docs/TILES.md` i jedno zdanie w `CLAUDE.md` przy *Tiles*: konwersja jest rzeczą, którą
   `ITileKind` obsługuje za darmo, i to jest argument za rejestrem — więc należy do jego opisu.

Szacunek: ~200 linii produkcyjnych, ~250 testów, **bez zmian w formacie layoutu i bez nowego interfejsu
kafla**.

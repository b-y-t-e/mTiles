# Usage tile: per-key 3-minute throttle + never blank on failure

## Problem (obecny stan)

`AiUsageService` (`src/mTiles/Services/AiUsageService.cs`) throttluje **całą rundę na raz**, nie
pojedyncze konto:

- `RefreshAsync(force: false)` odrzuca rundę, jeśli `DateTimeOffset.Now - _lastRefresh < RefreshInterval`
  (3 min) — ale to jedna wspólna klepsydra dla wszystkich źródeł.
- `RefreshAsync(force: true)` (przycisk Refresh w GUI, `UsageTileViewModel.Refresh`) **całkowicie
  pomija tę klepsydrę** i odpytuje WSZYSTKIE źródła na nowo, niezależnie jak dawno któreś z nich
  odpowiedziało. To jest udokumentowane i przetestowane zachowanie
  (`UsageTileTests.The_manual_refresh_asks_again`, `AiUsageService.RefreshAsync` XML-doc: *"a user
  pressing refresh has a reason this cannot see"*) — dokładnie to użytkownik chce zmienić.
- Gdy runda się kończy, `_reports` jest **całkowicie nadpisywane** wynikiem tej rundy
  (`RunAsync`, linie 179–191): `answers.OfType<AiUsageReport>()` odrzuca `null`-e (źródło rzuciło
  wyjątek albo go złapał `Ask`), więc konto, które wcześniej miało kartę z liczbami, po nieudanym
  odpytaniu **znika całkowicie** z listy — nie zostaje karta z ostatnią znaną wartością, nie zostaje
  nawet komunikat o błędzie.
- Miękka porażka (`AiUsageReport.Failed(...)`, np. "Nobody is signed in here") **też nadpisuje**
  poprzedni dobry raport tego konta — karta z liczbami zamienia się w kartę z jednym zdaniem, mimo że
  liczby sprzed 3 minut wciąż są aktualne w rozsądnym przybliżeniu.

## Cel (czego chce użytkownik)

1. **Globalnie, per klucz (konto)**: żadne pojedyncze źródło (Claude sign-in, Codex, OpenRouter
   instance, itd.) nie jest odpytywane częściej niż raz na 3 minuty — **także wtedy, gdy użytkownik
   naciśnie Refresh w GUI**. Refresh ma odświeżyć to, co jest "gotowe" do odświeżenia (starsze niż
   3 min), a nie hurtowo wszystko.
2. **Nigdy nie czyścić karty**: jeśli dla danego klucza mieliśmy już kiedyś udaną odpowiedź, a kolejna
   próba (timer albo Refresh) się nie powiedzie, karta ma **zostać pokazana z ostatnio wczytaną
   wartością** zamiast zniknąć / zamienić się w samo zdanie o błędzie.

## Kluczowa przeszkoda: źródła nie mają stabilnego Id przed odpytaniem

`IUsageSource` (`src/mTiles/Services/UsageSource.cs`) dziś nie ma żadnej synchronicznej,
przed-siecowej tożsamości — jedyne, co jest znane zanim `ReadAsync` odpowie, to `AccountKey` (nullable,
używane tylko do deduplikacji przed odpytaniem, nie jest tym samym co `AiUsageReport.SourceId`).
Żeby zdecydować "czy w ogóle pytać to źródło" **przed** wysłaniem żądania, potrzebny jest identyfikator
dostępny za darmo, z ustawień — dokładnie taki, jaki agent/provider i tak sam sobie liczy wewnątrz:

- `AiAgent.UsageSourceId(AiSignIn? signIn)` (protected, `src/mTiles/Services/Agents/AiAgent.cs:418`) →
  `signIn is null ? Id : $"{Id}:{signIn.Id}"`.
- `OpenRouterProvider.UsageSourceId(AiProviderInstance instance)` (public static,
  `src/mTiles/Services/Providers/OpenRouterProvider.cs:153`) → `$"openrouter:{instance.Id}"`.

Trzeba je **wypromować do interfejsu** (`IAiAgent.UsageSourceId`, `IAiProvider.UsageSourceId`), tak
żeby `IUsageSource.Id` mógł wywołać dokładnie tę samą formułę, którą i tak potem produkuje sam raport
jako `SourceId`. Inaczej klucz cache'a i `SourceId` w raporcie mogłyby się kiedyś rozjechać (np. przy
zmianie formatu w jednym miejscu, a nie w drugim).

## Projekt zmian

### 1. `IUsageSource.Id` — nowy, synchroniczny człon interfejsu

```csharp
public interface IUsageSource
{
    Task<AiUsageReport?> ReadAsync(CancellationToken ct = default);
    string? AccountKey => null;

    /// Stała tożsamość znana bez pytania nikogo — ten sam klucz, który źródło w końcu zwróci
    /// jako AiUsageReport.SourceId. Po tym kluczu AiUsageService pilnuje throttle'u per-konto.
    string Id { get; }
}
```

- `AgentUsageSource.Id => agent.UsageSourceId(signIn)`
- `ProviderUsageSource.Id => provider.UsageSourceId(instance)` (nowy member na `IAiProvider`,
  domyślna implementacja `=> instance.Id`, `OpenRouterProvider` nadpisuje jak dziś).

### 2. Cache per-źródło w `AiUsageService`

```csharp
private sealed record Entry(AiUsageReport? LastGood, DateTimeOffset LastAttemptAt);
private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);
```

- `LastGood` — ostatni raport, dla którego `Answered == true` (realne liczby), **nigdy** nie jest
  nadpisywany porażką ani miękkim `Problem`.
- `LastAttemptAt` — kiedy to źródło było **faktycznie** odpytane (bez względu na wynik) — to jest
  klucz throttle'u.

### 3. `RunAsync` — pytaj tylko to, co jest "gotowe"

Dla każdego źródła w bieżącej rundzie:

```
entry = cache.TryGetValue(source.Id)
if entry != null && now - entry.LastAttemptAt < RefreshInterval:
    // źródło jeszcze "świeże" — NIE pytaj, nawet jeśli force == true
    report = entry.LastGood            // ostatnia dobra wartość, jeśli jest
else:
    fresh = await Ask(source, ct, deadline)   // null | Failed-report | Answered-report
    cache[source.Id] = entry with { LastAttemptAt = now,
                                     LastGood = fresh is { Answered: true } ? fresh : entry?.LastGood }
    report = fresh is { Answered: true } ? fresh
           : entry?.LastGood ?? fresh          // porażka -> pokaż ostatnią dobrą wartość, jeśli jest
```

- **`force` już nie omija tego per-źródłowego okna** — steruje wyłącznie tym, czy w ogóle uruchomić
  rundę, gdy nic globalnie nie jest przeterminowane (patrz punkt 4). To jest świadoma zmiana zachowania
  opisanego dziś w komentarzu `RefreshAsync` ("a user pressing refresh has a reason this cannot see") —
  komentarz i test `UsageTileTests.The_manual_refresh_asks_again` trzeba zaktualizować, bo obecnie
  explicite asercjonują 2 wywołania z rzędu przy dwóch `force: true`.
- Źródło, które nigdy wcześniej nie było pytane (nowy sign-in, nowy provider instance), zawsze dostaje
  szansę — brak wpisu w cache = zapytaj.
- Wpisy cache'a dla źródeł, których już nie ma w bieżących ustawieniach (usunięty sign-in, usunięty
  provider), są czyszczone na końcu rundy, żeby nie rosły bez końca przez cały czas życia procesu.

### 4. `RefreshAsync` — round-level early-out

Obecny warunek (`!force && Now - _lastRefresh < RefreshInterval`) trzeba zastąpić pytaniem "czy
cokolwiek jest przeterminowane" zamiast globalnego `_lastRefresh`:

```
bool anyDue = sources.Any(s => !cache.TryGetValue(s.Id, out var e) || now - e.LastAttemptAt >= RefreshInterval);
if (!anyDue) return Task.CompletedTask;   // ani timer, ani Refresh nic tu nie zdziała
```

To dotyczy zarówno tickera timera, jak i ręcznego Refresh — jeśli wszystkie konta są świeże, runda się
w ogóle nie odpala (żadnego "pustego" przebiegu, `IsRefreshing` się nie zapali).

### 5. Zegar do testów

`RunAsync`/`RefreshAsync` dziś wołają `DateTimeOffset.Now` bezpośrednio — nie da się deterministycznie
przetestować granicy 3 minut. Dodać seam analogiczny do istniejącego `sources`:

```csharp
public AiUsageService(SettingsService settings, UsageHistory? history = null,
    Func<AppSettings, IReadOnlyList<IUsageSource>>? sources = null,
    Func<DateTimeOffset>? now = null)
```

domyślnie `() => DateTimeOffset.Now`, używane wszędzie zamiast `DateTimeOffset.Now` w tej klasie.
Testy dostają fake'a, który da się "przesunąć" w czasie między wywołaniami `RefreshAsync(force: true)`.

### 6. Czy trzeba nowe pole na `AiUsageReport`?

Nie jest to konieczne minimalnie: `MeasuredAt` zostaje niezmienione (to timestamp *ostatniej udanej*
odpowiedzi), a `UsageDisplay.Age` już dziś przygasza / stempluje starsze odczyty (patrz `docs`:
*"a reading older than the window it describes is stamped and dimmed"*) — to automatycznie da
wizualny sygnał "to nieaktualne" bez dodatkowej plumbingu.

Opcjonalnie (nice-to-have, nie wymagane): dodać `bool LastAttemptFailed` albo
`string? StaleReason` na `AiUsageReport`, żeby tile mógł np. pokazać małą ikonę "ostatnia próba się nie
powiodła" obok karty zamiast polegać wyłącznie na wieku `MeasuredAt`. Do decyzji przy implementacji —
nie blokuje głównego wymagania.

### 7. Logowanie

`Explain(report)` dziś loguje `Problem`. Warto dodać osobny `Trace.TraceInformation`, gdy runda
**pomija** źródło z powodu throttle'u per-klucz (diagnostyka: "czemu Refresh nic nie zrobił dla tego
konta") i gdy porażka jest **maskowana** starą dobrą wartością (żeby log nie milczał o tym, że pod
spodem coś nie działa, mimo że UI wygląda spokojnie).

## Pliki do zmiany

1. `src/mTiles/Services/UsageSource.cs` — `IUsageSource.Id`, implementacje na
   `AgentUsageSource`/`ProviderUsageSource`.
2. `src/mTiles/Services/Agents/IAiAgent.cs` + `AiAgent.cs` — wypromować `UsageSourceId` do interfejsu
   (dziś `protected`).
3. `src/mTiles/Services/Providers/IAiProvider.cs` + `AiProvider.cs` + `OpenRouterProvider.cs` — dodać
   `UsageSourceId` do interfejsu z domyślną implementacją, `OpenRouterProvider` nadpisuje istniejącą
   formułę.
4. `src/mTiles/Services/AiUsageService.cs` — cache per-źródło, zmieniona logika `RunAsync`/
   `RefreshAsync`, zegar jako zależność.
5. `tests/mTiles.Tests/UsageTileTests.cs`:
   - `The_manual_refresh_asks_again` — zmienia sens: dwa `force: true` z rzędu (bez przesunięcia
     zegara) mają teraz dać **1** wywołanie źródła, nie 2. Dodać osobny test z przesuniętym zegarem o
     >3 min, pokazujący że wtedy **drugi** `force: true` faktycznie pyta ponownie.
   - Nowy test: źródło raz odpowiada dobrze, potem (poza oknem throttle'u) `ThrowingSource`/`null` —
     `service.Reports` nadal zawiera **ostatni dobry raport**, a nie jest puste.
   - Nowy test: źródło raz odpowiada dobrze, potem w oknie throttle'u (force albo nie) w ogóle nie jest
     pytane — `CountingSource.Calls` zostaje na 1.
   - Zaktualizować XML-doc komentarz w `AiUsageService.RefreshAsync` (dziś tłumaczy stare zachowanie
     "musi zapytać, jakkolwiek świeże są dane" — trzeba przeformułować na "force odpytuje to, co jest
     przeterminowane, ale nigdy nie łamie 3-minutowego okna pojedynczego konta").

## Efekty uboczne / rzeczy do świadomej decyzji

- **`IsRefreshing` / working light** może teraz migać bardzo krótko albo wcale, gdy Refresh trafia na
  moment, w którym nic nie jest przeterminowane — runda w ogóle się nie odpala (`anyDue == false`).
  To jest zamierzone (nie ma sensu kręcić "pustą" rundą), ale warto to zauważyć w UI/UX — może się
  wydawać, że przycisk "nic nie zrobił".
- **`_lastRefresh` / `LastRefresh`** (globalny, używany przez tile do napisu "odświeżono o…") — do
  decyzji, czy ma się aktualizować tylko gdy faktycznie coś zostało zapytane, czy zawsze gdy runda się
  odpaliła (nawet jeśli wszystko wzięła z cache). Rekomendacja: aktualizować tylko gdy przynajmniej
  jedno źródło było realnie odpytane — inaczej napis "odświeżono przed chwilą" myliłby, sugerując
  świeże dane tam, gdzie były po prostu stare.
- Throttle jest per **Id źródła**, czyli per pojedyncze konto/klucz — różne konta w tym samym CLI
  (różne sign-iny) mają niezależne okna 3-minutowe, tak jak dziś różne raporty są niezależne.

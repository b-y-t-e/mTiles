# Synchronizacja CLAUDE.md / AGENTS.md — wnioski z researchu (2026-09)

> **Sekcje poniżej to notatki z researchu sprzed pomiarów i są miejscami nieaktualne**
> (m.in. `GEMINI.md`). Obowiązuje **Decyzja i plan (2026-09-03)** na końcu pliku —
> oparta na pomiarach zainstalowanych binarek.

## Trzy podejścia

1. **Kodowanie z jednego pliku kanonicznego** (AGENTS.md jako źródło prawdy, pozostałe pliki generowane)
2. **Symlink** — jeden plik, wiele nazw; wymaga `git config core.symlinks true` na Windows
3. **Shim z `@import`** — `CLAUDE.md` zawiera tylko linię `@AGENTS.md`; rozwiązuje tylko Claude Code

## Narzędzia

| Narzędzie | ⭐ | Podejście | Jak działa |
|---|---|---|---|
| [rulesync](https://github.com/dyoshikawa/rulesync) (npm) | ~1300 | kodowanie | katalog `.rulesync/` jako źródło, generuje konfigi dla ~40 narzędzi (reguły, commands, MCP, skills, permissions); `import`/`generate`/`convert` |
| [agentsync](https://github.com/PanisHandsome/ai-rules-sync) (npm, zero deps) | ~118 | kodowanie | jedna reprezentacja pośrednia (sekcje, globs, warnings); `sync --watch`, `sync --check` (CI), `sync --auto` (snapshot `.agentsync-state.json` wykrywa edytowany plik) |
| [agentlink](https://github.com/snapsynapse/agentlink/) | — | symlink | `.agentlink.yaml`: `source: AGENTS.md` → `CLAUDE.md`, `copilot-instructions.md`, `GEMINI.md`, także globalne `~/.claude/CLAUDE.md`; idempotentny, git hook |
| [sync-claude-md](https://github.com/lohn/sync-claude-md) | — | `@import` | obok każdego `AGENTS.md` tworzy `CLAUDE.md` = `@AGENTS.md`; pre-commit hook, nie zapisuje poza repo |
| [agents-sync](https://github.com/carlos-rodrigo/agents-sync) | — | symlink globalny | klonuje repo do `~/agents/`, symlinkuje globalne konfigi; aktualizacja = `git pull` |
| [claude-md-symlinker](https://github.com/osolmaz/claude-md-symlinker) | 12 | symlink | hook Claude Code tworzy symlinki w miarę przechodzenia po repo |

## Techniki wspólne

- **`@`-importy Claude Code**: ścieżki względne, max 5 poziomów, pierwsza akceptacja importu zewnętrznego. Cursor i Copilot ich **nie** rozumieją — tam symlink albo pełna kopia.
- **Egzekwowanie spójności obowiązkowe**: pre-commit hook lub CI (`--check` z niezerowym exit code), workflow triggerowany na oba pliki. Bez tego ktoś "naprawi" symlink na Windows zwykłą kopią i zaczyna się dryf.
- **Idempotentność**: nagłówek synchronizacji w generowanych plikach **bez** SHA/timestampa — inaczej churn przy każdej zmianie.
- **Konstrukty nietłumaczalne** (Cursor `globs` frontmatter, `@path`) → ostrzeżenia, nigdy ciche pominięcie.
- **Monorepo**: root = sekcje wspólne, per-pakiet = szczegóły; sekcje pakietu nadpisują root.

## Kompromisy

- **Symlink**: najczystszy (zero duplikacji), ale psuje się na Windows (zwykła kopia tekstu bez `core.symlinks`) i w niektórych setupach gita.
- **`@import` shim**: przenośny, działa na Windows, przetrwa git archive — ale tylko Claude Code go rozumie.
- **Pełna kopia (codegen)**: działa wszędzie, wymaga `--check` w CI, bo dryf jest kwestią czasu.

---

# Decyzja i plan (2026-09-03)

> **Wdrożone 2026-09-04.** `IAiAgent.SkillsDirectory`/`InstructionFile` (pięć pomiarów przypiętych
> w `AiAgentTests`), `Services/WorkspaceAgentFiles.cs`, `ClaudeLocalMdWriter` → `DatabaseSkillWriter`,
> plus jednorazowe sprzątnięcie `claude.local.md` i sekcji `# Database access` z `AGENTS.md`,
> oraz wpis w `.gitignore` na nasz własny podkatalog skilla (tylko w repozytorium, przez
> `GitIgnoreFile` — ten sam oznaczony blok co `.mtiles/`).
> Nie wdrożone: CI sprawdzające shim, przeniesienie tego repozytorium na kanon `AGENTS.md`,
> oraz pomiary z rozdziału 7 (bramka zaufania w pi i codex).

Pomiary poniżej zrobione na binarkach zainstalowanych na tej maszynie: agy 1.1.22,
opencode, claude, codex (`@openai/codex`), pi (`@earendil-works/pi-coding-agent`).
**Każda z nich to cudzy kontrakt, który już się ruszał** — objawem zmiany będzie plik,
którego nikt nie czyta, i cisza. Stąd tabele, nie pamięć.

## 1. Co który agent naprawdę czyta

| CLI | plik instrukcji projektu | uwagi |
|---|---|---|
| opencode | `AGENTS.md` | walk w górę; `CLAUDE.md` tylko globalny `~/.claude/CLAUDE.md` |
| codex | `AGENTS.md` | + `AGENTS.override.md` (patrz niżej) |
| pi | `AGENTS.md` | kandydaci: `AGENTS.override.md`, `AGENTS.md`, `AGENTS.MD`, `CLAUDE.md`, `CLAUDE.MD` |
| agy | `AGENTS.md` **lub** `GEMINI.md` | własna dokumentacja **zaleca** `AGENTS.md` |
| claude | `CLAUDE.md` + `CLAUDE.local.md` | **nie czyta `AGENTS.md`** |

`GEMINI.md` jest **niepotrzebny** — agy czyta `AGENTS.md`.

**Claude Code nie ma discovery `AGENTS.md`.** Trafienia na tę nazwę w jego binarce to
wyłącznie importer z codexa (`codex:project:instructions`, src `AGENTS.md` → target
`CLAUDE.md`) — jednorazowa migracja. Ładowanie jest twarde:
`case "Project": return join(dir,"CLAUDE.md")`, `case "Local": return join(dir,"CLAUDE.local.md")`.

**`AGENTS.override.md` (pi, codex) NIE jest odpowiednikiem `CLAUDE.local.md`.**
To lista pierwszeństwa, nie sumowanie — z kodu pi:

```js
const candidates = ["AGENTS.override.md","AGENTS.md","AGENTS.MD","CLAUDE.md","CLAUDE.MD"];
for (const filename of candidates) { ... }   // pierwszy znaleziony wygrywa
```

codex mówi to samo słowami: *„respecting normal project-document precedence
(`AGENTS.override.md`, `AGENTS.md`, then configured fallback filenames)"*.
Wpisanie tam czegokolwiek **skasowałoby użytkownikowi instrukcje projektu**, cicho.

**Pliku `AGENTS.local.md` nie ma nigdzie.** 0 trafień w agy, opencode, claude, codex, pi.
Nie jest niczyją konwencją i nie należy go wymyślać — byłby to `CLAUDE.local.md` pod
nazwą obiecującą uniwersalność, której nie ma.

## 2. Synchronizacja: `AGENTS.md` kanonem, jeden shim

```
AGENTS.md   # kanon, jedyny plik z treścią
CLAUDE.md   # shim: jedna linia `@AGENTS.md`
```

Symlink odpada (Windows bez `core.symlinks` materializuje plik tekstowy ze ścieżką w
środku — agent czyta to jako całość instrukcji, i psuje się cicho). `rulesync`/`agentsync`
odpadają — to generatory pełnych kopii, a nie ma czego generować: cztery z pięciu agentów
czytają kanon bez niczego.

**Shim jest bezpieczny — zmierzone.** Żaden agent nie zobaczy dosłownego `@AGENTS.md`
jako instrukcji: pi sięga po `CLAUDE.md` dopiero gdy nie ma `AGENTS.md` (a kanon jest
zawsze), opencode projektowo szuka wyłącznie `AGENTS.md`, agy `AGENTS.md`/`GEMINI.md`,
claude wyłącznie `CLAUDE.md`. To nie było oczywiste i mogło zabić cały pomysł.

### Dlaczego NIE synchronizacja dwukierunkowa po mtime

Rozważone i odrzucone: obserwować oba pliki i kopiować treść z tego, który zmienił się
później. Cztery powody:

1. **Shim nie synchronizuje — on eliminuje drugą treść.** Nie ma czego wykrywać ani co
   kopiować; `CLAUDE.md` ma jedną linię i nigdy się nie zmienia.
2. **Dwie pełne kopie w gicie** — podwojone diffy i konflikty przy każdym merge'u.
3. **Działa tylko gdy mTiles chodzi.** Edycja w innym edytorze, `git pull`, praca na
   maszynie bez mTiles → dryf, o którym nikt się nie dowie.
4. **Jednoczesna zmiana obu plików gubi jedną stronę, cicho.** `git checkout`, `git pull`
   i `git stash pop` dotykają obu naraz; „najnowszy wygrywa" to wtedy rzut monetą.

Jedyne miejsce, gdzie pogodzenie treści jest naprawdę potrzebne, to **repo, w którym oba
pliki już istnieją z różną treścią**. To jednorazowa migracja z pytaniem do użytkownika
(który jest kanonem), nie mechanizm ciągły. Dopóki nie odpowie — nie ruszamy niczego.

Egzekwowanie na co dzień: pre-commit + CI sprawdza, że `CLAUDE.md` to dokładnie
`@AGENTS.md` i nic więcej. Bez SHA/timestampa w shimie — inaczej churn przy każdym commicie.

### Shim też jest zależny od kafelków

`CLAUDE.md` nie ma powstawać w projekcie, w którym nikt nie używa Claude Code — z tego
samego powodu, dla którego `.opencode/skills` nie ma powstawać bez kafelka opencode.
Czyli agent odpowiada na **dwa** pytania, a nie jedno, i obsługuje je ta sama usługa tą
samą regułą „policz zbiór od nowa, skasuj różnicę" (rozdział 5).

## 3. Sekcja bazodanowa przestaje być sekcją w pliku, staje się skillem

Dziś `ClaudeLocalMdWriter` wstrzykuje `# Database access` do `claude.local.md` i
`AGENTS.md`. Trzy rzeczy są z tym nie tak:

1. **Dane maszynowe w commitowanym pliku** — port i nazwy serwerów lądują w repo.
2. **`claude.local.md` małą literą.** Claude Code otwiera literalnie `CLAUDE.local.md`.
   Na Windows przechodzi, **na Linuksie to inny plik** — sekcja jest tam niewidoczna.
3. **Wycinanie sekcji z cudzego pliku** przez dopasowanie nagłówka — cała klasa błędów
   „nie trafiliśmy w granice".

Skill rozwiązuje wszystkie trzy: usunięcie to skasowanie katalogu, `AGENTS.md` zostaje
wyłącznie tym, co napisał człowiek, a `claude.local.md` znika razem z bugiem wielkości
liter — bez naprawiania.

**Czwarta wygrana: skill może powiedzieć znacznie więcej.** Dziś sekcja jest celowo
skąpa, bo siedzi w kontekście przy każdej turze. Skill ładuje się na żądanie, więc mieści
pełny kontrakt, którego agent teraz w ogóle nie zna: co blokuje `SqlGuard`
(DROP/TRUNCATE/ALTER zawsze), co znaczy RW/RO, że **zapis bez uprawnień wyświetla dialog
użytkownikowi i zapytanie czeka**, limity 50k wierszy / 16MB / 512KB body, zakaz
`sp_executesql`.

### Katalogi skilli (zmierzone)

| CLI | katalog projektowy |
|---|---|
| claude | `.claude/skills/<name>/SKILL.md` |
| opencode | `.opencode/skills/<name>/SKILL.md` (konfigurowalne przez `paths`; to domyślna) |
| codex | `.agents/skills` |
| pi | `.agents/skills` (cwd lub przodek) |
| agy | `{workspace}/.agents/skills/{skill_name}/SKILL.md` |

**Trzy agenty dzielą `.agents/skills`.** To jest źródło jedynej nietrywialnej reguły w
całym planie — patrz rozdział 5.

### Cena i jak ją płacimy

Sekcja w `AGENTS.md` jest w kontekście **bezwarunkowo**. Skill jest w kontekście jako
nazwa + opis; treść dopiero gdy model **sam uzna**, że jest potrzebna. Na pytanie „czemu
suma zamówienia się nie zgadza" agent równie dobrze przeczyta kod i nigdy nie otworzy
skilla — bo nie wie, że ma dostęp do żywych danych.

Dlatego **opis to nie metadana, to jedyny trigger**, i nie może brzmieć jak kategoria:

```yaml
---
name: mtiles-database
description: >
  Odpytuj bazy tego projektu (ERP_PROD na SQL Server, analytics na PostgreSQL)
  po lokalnym moście HTTP mTiles. Użyj zawsze, gdy potrzebujesz prawdziwych
  danych, schematu tabeli, nazw kolumn albo weryfikacji zapytania — zamiast
  zgadywać strukturę z kodu lub migracji.
---
```

Celowe są dwie rzeczy: **konkretne nazwy baz w opisie** (to one łapią intencję, nie słowo
„database") i **zdanie mówiące, kiedy użyć tego zamiast czego** — domyślną alternatywą
modelu jest czytanie kodu i trzeba ją nazwać wprost.

Nazwa jako **stały slug** (`mtiles-database`), nigdy generowana z zestawu baz — inaczej
zmiana zestawu przemianowuje skill i zostają sieroty.

### Żadnego wskaźnika w `AGENTS.md` — kafel nie dotyka plików instrukcji

Rozważone i **odrzucone**: dopisywanie jednej linii „bazy są, patrz skill `mtiles-database`"
do `AGENTS.md`, żeby wzmocnić wykrywalność.

Niepotrzebne, bo **skille są wykrywane automatycznie** — każdy z pięciu CLI dostaje listę
nazw i opisów skilli projektowych bez proszenia. Agent widzi `mtiles-database` razem z
opisem wymieniającym konkretne bazy, więc wskaźnik byłby drugą kopią tej samej informacji.

I szkodliwe. Większość repozytoriów ma dziś `CLAUDE.md` i nie ma `AGENTS.md`. Wskaźnik
**utworzyłby** tam `AGENTS.md` z jednym zdaniem o bazach — a pi, codex i agy czytają
`AGENTS.md` i nie otwierają `CLAUDE.md`, więc od tej chwili widziałyby jedno zdanie o
bazach **zamiast** instrukcji projektu. Włączenie dostępu do baz odcinałoby trzy agenty od
dokumentacji, po cichu.

Bez wskaźnika: **kafel bazodanowy pisze wyłącznie katalogi skilli i nigdy nie dotyka
`AGENTS.md` ani `CLAUDE.md`** — a synchronizacja plików instrukcji (rozdział 2) przestaje
mieć cokolwiek wspólnego z bazami. Dwa niezależne tematy.

Jeśli pomiar z rozdziału 7 wykaże, że pi albo codex bramkują skille zaufaniem,
odpowiedzią jest komunikat **na kaflu**, nie wskaźnik w pliku instrukcji — wskaźnik i tak
by nie pomógł, skoro skill byłby wtedy niedostępny.

## 4. Gdzie mieszka logika: na klasie agenta

To jest ten sam kształt co `SignInEnv`, `SessionIdForTile` czy `EffortArgs` — pytanie, na
które tylko dane CLI zna odpowiedź, zadane raz i zmierzone raz.

```csharp
// IAiAgent
/// <summary>Gdzie ten CLI szuka skilli projektowych, albo null gdy żadnych nie czyta.</summary>
string? SkillsDirectory(string workspaceDir) => null;

/// <summary>Plik instrukcji projektu, który ten CLI otwiera. Kanon to AGENTS.md;
/// agent, który go nie czyta, nazywa tu swój własny i dostaje shim.</summary>
string InstructionFile => "AGENTS.md";
```

Domyślnie `null` / kanon — jak `UsageAsync`. Nowy agent, który o tym zapomni, nie dostaje
skilla i czyta `AGENTS.md`; zapomnienie idzie w stronę „mniej", nigdy w stronę pisania w
nieznane miejsce. **Odwrotnie niż `EnvFor`**, które celowo nie jest wirtualne — tam
zapomnienie było groźne, tu jest tylko stratne.

Agent odpowiada **gdzie**, kafel bazodanowy **co**. `Services/Agents/` nie dowiaduje się
o istnieniu baz — dostaje ścieżkę workspace'u i zwraca ścieżkę, tak jak `SignInEnv`
zwraca blok zmiennych, nie wiedząc po co komu. Treść `SKILL.md` jest jedna dla wszystkich
pięciu.

## 5. Konsolidacja: `WorkspaceAgentFiles`

**Źródłem prawdy jest drzewo kafelków tego workspace'u, nie to, co jest zainstalowane na
maszynie.** Projekt, w którym używasz wyłącznie Claude'a, dostaje `.claude/skills` i
`CLAUDE.md`, i nic poza tym — zakładanie `.opencode/skills` w cudzym repo dla narzędzia,
którego nikt tu nie używa, to śmiecenie.

Nie może to jednak siedzieć w kaflu bazodanowym, bo **trzy agenty dzielą `.agents/skills`**:
usunięcie kafelka pi nie może skasować tego katalogu, dopóki w workspace stoi kafelek
codexa albo agy. Reguła jest więc nie „usuń katalog usuniętego agenta", tylko **„policz
zbiór ścieżek od nowa i skasuj różnicę"** — a to wymaga jednego miejsca, które zna cały
workspace.

Stąd `Services/WorkspaceAgentFiles.cs` — jeden na workspace, ta sama kategoria co
`WorkspaceGitWatcher`:

- **Czyta drzewo kafelków**, wybiera kafelki agentów, pyta każdy o `SkillsDirectory`
  i `InstructionFile`, zwraca **zbiory unikalnych ścieżek** (`Distinct`,
  `OrdinalIgnoreCase`) — `.agents/skills` raz, nie trzy razy.
- **Reaguje na zmiany drzewa**: dodanie kafelka pi zakłada katalog od razu, usunięcie
  ostatniego kafelka pi go sprząta — bez restartu. Tak samo shim `CLAUDE.md` pojawia się
  z pierwszym kafelkiem Claude'a i znika z ostatnim.
- **Przelicza zbiór po każdej zmianie i kasuje różnicę.** To jest cała obrona przed
  pułapką `.agents/skills`.

### Zapis i kasowanie są asymetryczne, i to nie jest niedopatrzenie

| kiedy | zakres |
|---|---|
| zapis / aktualizacja | **tylko ścieżki kafelków obecnych w tym workspace** |
| kafelek agenta zniknął | przelicz zbiór, skasuj **różnicę** (nie „katalog tego agenta") |
| **dostęp do baz wyłączony** | skasuj skill ze **wszystkich znanych ścieżek**, bez patrzenia na kafelki |

Ostatni wiersz jest regułą bezpieczeństwa, nie sprzątaniem: **żaden agent nie może przez
przypadek dowiedzieć się o dostępie do baz, gdy tego dostępu nie ma.** Lepiej skasować
coś, czego nie ma, niż zostawić żywy adres bazy w katalogu, o którym nikt już nie pamięta
— po odinstalowaniu CLI, po przesiadce na inną maszynę, po przeniesieniu repo.

Wyzwalacze wyłączenia: odznaczenie ostatniej bazy w kaflu **i** wyłączenie usługi
bazodanowej globalnie. Oba prowadzą do tego samego, ślepego kasowania.

**Uwaga: to dotyczy skilla, nie shimu.** `CLAUDE.md` nie zawiera nic wrażliwego, więc
kasowanie „na wszelki wypadek" go nie obejmuje — znika normalną drogą, gdy zniknie
ostatni kafelek Claude'a.

## 6. Zmiany w kodzie

- `IAiAgent.SkillsDirectory(workspaceDir)` + `IAiAgent.InstructionFile`, 5 implementacji
  (3 zwracają `.agents/skills`; tylko `ClaudeAgent` nadpisuje `InstructionFile`).
- `Services/WorkspaceAgentFiles.cs` — konsolidacja opisana wyżej.
- `ClaudeLocalMdWriter` → `DatabaseSkillWriter`. Przestaje zasługiwać na starą nazwę.
  `TargetFiles` znika; zostaje wyłącznie budowanie treści `SKILL.md`. Kafel nie zapisuje
  już do żadnego pliku instrukcji.
- `claude.local.md` — **usunięcie**, nie naprawa wielkości liter.
- `.gitignore`: ignorować **tylko nasz podkatalog** (`.claude/skills/mtiles-database/`
  itd.), nigdy całe `.claude/skills` — tam użytkownik trzyma swoje. Precedens:
  `GitIgnoreFile` robi to samo dla `.mtiles/`.
- Jednorazowe pogodzenie `CLAUDE.md` i `AGENTS.md` w repo, gdzie oba mają treść —
  z pytaniem do użytkownika, który jest kanonem. Bez odpowiedzi: nie ruszamy.
- CI: shim `CLAUDE.md` == `@AGENTS.md`.
- `AiAgentTests`: pin pięciu ścieżek i pięciu plików instrukcji, tak jak reszty
  zmierzonych tabel. Przy okazji pilnuje, że `.agents/skills` **ma co deduplikować**.

## 7. Do zmierzenia na żywo przed wdrożeniem

1. **Bramka zaufania w pi i codex.** Z kodu pi: *„must be gated by project trust:
   trust-requiring entries under cwd/.pi, or `.agents/skills` in cwd or one of its
   ancestors"*; codex ma `trustStatus`. Czyli skill utworzony przez kafel może wymagać
   zatwierdzenia przez użytkownika przy pierwszym uruchomieniu. Jeśli tak — trzeba to
   powiedzieć **na kaflu**, bo inaczej jest to cichy tryb awarii.
2. **Czy opis rzeczywiście triggeruje.** Jedyny sposób to odpalić realne pytanie o dane
   w workspace ze skillem i sprawdzić, czy agent go otworzy zamiast czytać kod.

## 8. Decyzja zaktualizowana (2026-09-04): shim zastąpiony pełną dwukierunkową synchronizacją

**Rozdział 2 powyżej jest historyczny.** Shim (`CLAUDE.md` = jedna linia `@AGENTS.md`) został
usunięty z `WorkspaceAgentFiles` i zastąpiony osobnym mechanizmem: `Services/AgentFileSyncEngine.cs`
(jeden na workspace, `FileSystemWatcher` na dokładnie dwie nazwy plików w katalogu głównym) i
`Services/AgentFileSyncCoordinator.cs` (jeden na aplikację, decyduje kiedy zapytać i trzyma
silniki dla załadowanych workspace'ów). `WorkspaceAgentFiles` wraca do tego, czym było przed
rozdziałem 2 — samych katalogów skilli.

Powód zmiany: użytkownik chciał, żeby oba pliki **zawsze** niosły tę samą, pełną treść — nie import,
tylko dwukierunkowe lustro — z jawną zgodą per-workspace i kreatorem rozstrzygającym konflikt, gdy
oba pliki już istnieją z różną treścią.

**Cztery powody przeciw synchronizacji z rozdziału 2 — dlaczego już nie obowiązują:**

1. *"Shim nie synchronizuje"* — to teraz cel, nie wada: `AgentFileSyncEngine.ReconcileAsync`
   rzeczywiście kopiuje treść w obie strony.
2. *"Dwie pełne kopie w gicie, podwojone diffy"* — zaakceptowane świadomie; to jest teraz cena,
   którą użytkownik wybrał, płacąc za identyczną treść w obu plikach.
3. *"Działa tylko gdy mTiles chodzi"* — nadal prawda i nadal ograniczenie: silnik żyje tylko, gdy
   dany workspace jest załadowany (`AgentFileSyncCoordinator.Unload` go zatrzymuje). Edycja w innym
   edytorze na maszynie bez mTiles nadal nie jest widziana — ale przy następnym otwarciu workspace'u
   w mTiles pierwsza zmiana, jaką silnik zobaczy po starcie, i tak trafia do drugiego pliku.
4. *"Jednoczesna zmiana obu plików gubi jedną stronę po cichu"* — rozwiązane jawną regułą: silnik
   propaguje na podstawie **rzeczywistego mtime** ("zawsze od najnowszego"), a **usunięcie** jednego
   pliku przy włączonej synchronizacji jest odtwarzane z drugiego, nigdy nie czytane jako rezygnacja
   z synchronizacji — na to służy osobny przełącznik (menu kontekstowe workspace'u / Ustawienia →
   Ogólne).

**Pogodzenie treści różniącej się nie jest już jednorazową migracją poza mechanizmem — jest jego
częścią.** Kreator (`Views/AgentFileSyncWizard`) pyta, który plik jest aktualny (pokazując rozmiar i
datę modyfikacji), gdy oba pliki istnieją z różną treścią — zarówno przy pierwszym otwarciu
workspace'u, jak i przy ręcznym włączeniu synchronizacji z menu kontekstowego.

**Zapobieganie pętli zapis→watcher→zapis jest pamięcią, nie blokadą zapisu.** Każdy zapis silnika
natychmiast aktualizuje własną pamięć podręczną (mtime + treść) tej ścieżki, więc kolejne zdarzenie
`FileSystemWatcher` na tej samej ścieżce — wywołane przez ten właśnie zapis — czyta ten sam mtime,
który już ma w pamięci, i nic nie robi. Prawdziwa zewnętrzna edycja to jedyna rzecz, która zostawia
mtime różny od zapamiętanego.

**Globalny wyłącznik i ustawienie per-workspace to dwie osobne rzeczy.** `AppSettings.AgentFileSyncEnabled`
(domyślnie włączony, Ustawienia → Ogólne) jest nadrzędny — wyłączenie go zatrzymuje każdy załadowany
silnik i blokuje kreator wszędzie. Każdy workspace ma własny plik `.mtiles/agent-file-sync.json`
(`Enabled` + `WizardAnswered`) — brak pliku znaczy "nigdy nie pytano", nie "wyłączone".

**Wyłączony globalny przełącznik wycisza pytanie, nie odpowiada na nie.** Dopóki jest wyłączony,
`AgentFileSyncPolicy.Decide` zwraca `None` i nic nie trafia na dysk — więc ponowne włączenie jest
dokładnie tym momentem, w którym załadowane workspace'y stają się pytalne, i `OnSettingsChanged`
przepuszcza je przez pełną ocenę, a nie tylko przez start/stop silników. Inaczej workspace, którego
nigdy nie zapytano, czekałby na zmianę drzewa kafelków albo ponowne otwarcie.

**Plik, którego nie da się odczytać, to trzecia odpowiedź** — nie "nie istnieje". Uzgodnienie nie
zapisuje niczego i **samo uzbraja kolejną próbę** (do pięciu): zdarzenie, które je wywołało, jest już
skonsumowane, więc plik chwilowo trzymany przez edytor, antywirusa albo klienta chmury zostawiłby oba
pliki cicho rozjechane aż do następnego zapisu użytkownika. Ograniczenie do pięciu prób jest po to,
żeby plik zablokowany na stałe nie uzbrajał timera do końca sesji.

**Każde uruchomienie silnika dostaje własny budżet prób.** Licznik nieudanych odczytów jest zerowany
przy starcie, nie tylko po udanym odczycie: `Stop()` + `StartAsync()` — czyli globalny przełącznik i
odpowiedź kreatora dla działającego lustra — trafiałyby inaczej w licznik wyczerpany przez poprzedni
przebieg i poddawały się bez uzbrojenia ani jednej próby.

**Lista śledzonych workspace'ów to suma dwóch tablic, nie sam rejestr silników.** `_agentsSeen` zna
workspace oceniony (nawet jeśli nigdy nie potrzebował silnika), `_engines` — ten, dla którego silnik
wystartował przełącznik z menu wiersza, którego drzewa kafelków nikt tu nie widział. Czytanie samego
`_engines` po cichu wypuszczałoby z ponownej oceny dokładnie te workspace'y, którym globalny
przełącznik ją jest winien.

**Trzy klasy zamiast jednej.** `AgentFileSyncConfigStore` (gdzie mieszka odpowiedź i w jakim
kształcie) i `WorkspaceWorkGate` (jedna decyzja na workspace naraz, z *generacją załadowania*, bo
decyzja rozciągnięta na kreator nie może dotykać workspace'u odładowanego w międzyczasie) są osobno od
koordynatora: każda z nich zmienia się z innego powodu niż reguły pytania.

**Start silnika to uzgodnienie, nie samo zasianie cache'u.** Gdy aplikacja nie działa, nikt nie
obserwuje plików — `git pull`, `checkout` albo edycja w innym narzędziu potrafi je rozjechać. Zapisanie
takiego stanu do cache'u czyni z rozjazdu nowe „bez zmian", więc pierwsza późniejsza edycja jednej
strony po cichu nadpisałaby offline'owe zmiany drugiej. Para, która się zgadza, trafia do cache'u i nic
nie jest zapisywane; para, która się nie zgadza, zostaje poza cache'em i jest uzgadniana tą samą regułą
„zawsze od najnowszego", już po uruchomieniu obserwatora — żeby własny zapis silnika wrócił do niego
jako własny.

**`SettingsChanged` leci z każdej właściwości okna Ustawień** (per naciśnięty klawisz w polu tekstowym),
a ta funkcja czyta z niego jedno pole. Koordynator pamięta więc ostatnią wartość globalnego przełącznika
i pełną ocenę robi tylko wtedy, gdy przełącznik naprawdę się ruszył.

**Zasiew należy do silnika.** Odpowiedź kreatora ("ten plik jest aktualny") trafia do
`AgentFileSyncEngine.StartAsync(authoritativeFileName)`, a nie do własnej kopiującej ścieżki w
koordynatorze: to jest to samo uzgodnienie co każda późniejsza edycja, tylko ze zwycięzcą wskazanym
zamiast wyliczonym z mtime. Reguła mieszka więc w jednej klasie — tej, która ma testy.

**Własny zapis wraca do cache'u tylko wtedy, gdy z dysku wraca to, co zapisano.** Edycja trafiona w
okno między zapisem a odczytem byłaby zapamiętana jako własny zapis silnika, czytana potem jako "bez
zmian" i nigdy nieprzeniesiona na drugą stronę — oba pliki zostałyby trwale rozjechane. Pozostawiona
poza cache'em, jest propagowana przez najbliższe uzgodnienie.

**Zasiew zostawia kopię pliku, który przegrywa.** To jedyny zapis, który potrafi zastąpić treść nigdy
przez nas nienoszoną — np. niezacommitowany `AGENTS.md` — a użytkownik odpowiada na pytanie "który plik
jest aktualny", a nie zgadza się na utratę drugiego. Przegrywający ląduje więc obok jako
`<nazwa>.pre-sync-<data>` (ta sama reguła co `{id}.pre-kind.json`), kreator mówi o tym nad przyciskami,
a kopia, której nie da się zapisać, **wstrzymuje** nadpisanie zamiast puszczać je bez zabezpieczenia.
Każde późniejsze lustrzane odbicie to już synchronizacja, którą użytkownik włączył — bez kopii.

**Każde uruchomienie silnika ma numer** (`_epoch`). Odpowiedź kreatora dla już działającego lustra to
`Stop()` i zaraz po nim `StartAsync(authoritative)` na tej samej instancji, więc uzgodnienie
zakolejkowane tuż przed `Stop()` widziałoby `IsRunning` znów jako `true` i rozstrzygnęłoby parę po
mtime — nadpisując dokładnie ten plik, który użytkownik właśnie wskazał jako aktualny. Uzgodnienie
niesie więc numer uruchomienia, pod którym je zakolejkowano, i przedawnione nie robi nic.

**Błąd watchera to przebudowa, nie rezygnacja.** Filtry nazw stosowane są w kodzie zarządzanym, więc
natywny bufor zbiera wszystko, co produkuje katalog, i duży `git checkout` w korzeniu workspace'u
wystarcza, by go przepełnić — a to straty zdarzeń, nie martwy serwer lustra. `Stop()` na `Error`
zostawiałby oba pliki wolno rozjeżdżać się do końca sesji, podczas gdy config dalej mówi `Enabled=true`
i menu kontekstowe pokazuje synchronizację jako włączoną; kafel git w tym samym repozytorium odpowiada
na to samo zdarzenie odświeżeniem, a nie poddaniem się. Odpowiednikiem odświeżenia jest tu przebudowa:
świeży watcher i zasiew, czyli to samo uzgodnienie, które dostaje okno offline. Ograniczone tak jak
`RelaunchBudget` — trzy na dziesięć minut, stawką po oknie, nie sumą — bo system plików, którego
watchery padają tak szybko, jak się je podnosi, nie może kręcić kółem. Bufor jest też wielkości tego,
co nosi watcher gita w tym samym katalogu (51200).

**Sprawdzenie epoki tylko przed I/O nie domyka wyścigu.** `Stop()` nie czeka na bramę uzgodnienia, więc
uzgodnienie, które weszło do sekcji krytycznej tuż przed `Stop()` + `StartAsync(authoritative)`,
dokoczyłoby mirror po mtime — a zasiew zastałby parę już zgodną, `alreadyAgrees=true`, i nigdy nie
wywołałby uzgodnienia z nazwą wskazaną przez użytkownika: wybór przepadłby bez kopii `.pre-sync-`,
czyli dokładnie to, przed czym `_epoch` miało chronić. Epoka jest więc pytana jeszcze raz po odczycie
obu plików i jeszcze raz po kopii zapasowej zasiewu — po każdym `await`, w którym odpowiedź może
wpaść — a przedawnione uzgodnienie, które już oba pliki przeczytało, nie robi nic wcale.

**Shim znika tylko tam, gdzie naprawdę ruszy silnik.** Przełącznik z menu wiersza da się nacisnąć przy
wyłączonym globalnym przełączniku (widoczność pozycji menu liczona jest w chwili budowania menu), a
cały ten ciąg istnieje po to, by silnik wystartował — więc przy wyłączonym przełączniku zapisuje
odpowiedź i nie rusza plików: skasowanie shimu przed startem, który nigdy nie nastąpi, zostawiłoby
workspace bez jedynego pliku, jaki Claude Code czyta, i nic go tu nie odtwarza. Bezpiecznikiem dla
odpowiedzi, która poczekała, jest sam start silnika: zabiera napotkany shim i wskazuje `AGENTS.md` jako
aktualny, bo plik nie niosący żadnych słów użytkownika nie jest jedną z dwóch wersji do rozstrzygnięcia
po mtime.

**Shim bez `AGENTS.md` obok to nadal shim.** Stara linia `@AGENTS.md` importuje plik, którego nie ma,
wię nie niosąca żadnych słów użytkownika jest niezależnie od tego, czy cel istnieje — a nierozpoznana
(docelowo wymagano jego obecności) trafia do kreatora jako jedna z dwóch wersji i jest zasiewana do
nowego `AGENTS.md`, którego cała treść to cykliczne `@AGENTS.md`, czytane potem przez codexa, pi i agy
jako instrukcje projektu. Rozpoznanie patrzy więc tylko na treść `CLAUDE.md`; włączenie synchronizacji
zabiera shim i nie sieje niczego — obie strony lustra startują puste, a pierwszy zapis po którejkolwiek
stronie przenosi się jak zwykle. Odmowa (i wyłączony globalny przełącznik) zostawia plik bez zmian.

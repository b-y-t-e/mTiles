# Roadmap

Things worth doing that nothing is currently blocked on. Each entry says what is wrong now, what the
stopgap is, and what would actually settle it — so that picking one up does not start with rediscovering
why it is here.

---

## A control that is a combo box and a search box at once

**Where it hurts.** The agent instance form's **Model** and **Fast model** fields (Settings → AI), and
the manual-connection form's database name if it ever grows a list.

**Why neither built-in control is right.** Avalonia 12.1.1 offers two, and each is wrong in the opposite
direction — both measured, not assumed:

- **`AutoCompleteBox`** — what is used today, with `FilterMode="Custom"` over `Views/ModelSearch.cs`:
  every typed word, anywhere in the id, in any order. That is the only thing that makes OpenRouter's
  catalogue usable — it answered with **396 models** when this was written. But the control has **no
  affordance at all** — no arrow, nothing to click — so a field with a perfectly good list behind it
  looks identical to an empty text box, and there is no way to ask "what are my options" without
  guessing a first letter.
- **`ComboBox IsEditable="True"`** — `IsEditable` and `Text` both exist in 12.1.1 (compile-checked), so
  switching is one attribute. It has the arrow, and it lets you type. But its typing is
  `IsTextSearchEnabled` — *jump to the first entry starting with what you typed* — and **not**
  filtering. On 396 models that is a 396-row scroll list with a keyboard shortcut to the letter B. It
  trades the thing that made the field usable for the thing that made it discoverable.

**What has changed since.** The **List** button is gone: the list is fetched when the account changes,
so there is no longer a press that has to visibly do something, and the `IsModelListOpen` stopgap that
opened the drop-down went with it. The reported symptom is therefore gone — but the gap this entry
exists for is not, and is now the whole of it: **the field still shows no sign that a list exists**
until somebody types into it.

**What would settle it.** A control that shows a drop-down affordance, opens the full list on click, and
narrows it as you type. In preference order:

1. **Find one.** Worth checking before writing anything: whether a later Avalonia gives
   `AutoCompleteBox` a toggle button in its template (this may be a theme-level fix — a
   `ControlTemplate` override on the Fluent theme's `AutoCompleteBox`, no new control at all, which
   would be by far the cheapest answer and should be tried first), and what the community control sets
   offer.
2. **Template it.** If the Fluent template can carry a chevron that sets `IsDropDownOpen`, this is a
   dozen lines in `Styles/Controls.axaml` and no new type. **Try this before option 3.**
3. **Write it.** A `TemplatedControl` wrapping `AutoCompleteBox` plus a toggle button, in
   `Views/`, or — if it earns it — in its own package alongside `Terminal.Avalonia`. The behaviour
   wanted is small and well understood: click the chevron → open with the unfiltered list; type →
   filter; Escape → close and keep the text.

**Not urgent.** Both fields are used when an instance is created or edited, which is a handful of times
per installation, and the stopgap covers the case where somebody has asked for the list. It goes on this
list rather than into the next change because a new control is a maintenance commitment, and the third
option is the one most likely to be reached for and the one least worth reaching for first.

---

## A tile header that is an index, a kind and a description

**Where it is now.** The header is a kind glyph, the tile's name, and — since the note landed — what an
agent tile is running (`IDescribedTile.HeaderNote`, e.g. `Claude Code · glm-5.3-flash`). The name is
still doing two jobs: it is both the label the user may type and the generated `Agent#1` that nobody
chose. The end state wanted is three separate things: **an index**, **what kind of tile this is**, and
**a description the user writes**.

### 1. The index, and reading order

Every tile in the open workspace gets a position label, assigned **in reading order** — left to right,
then down — so the labels match how the layout looks rather than the order the tiles were created in.
The alphabet is the keyboard's own: `1 2 3 4 5 6 7 8 9 0`, then `q w e r t y u i o p`, then
`a s d f g …`. Ten tiles is already a busy screen and thirty is the practical ceiling; past that the
label is simply absent, which is honest and costs nothing.

**Reading order is the part with a real decision in it.** The layout is a binary tree of splits, not a
grid, so "left to right, then down" is not something the tree answers directly — it is a rule about
where each leaf's rectangle *ended up*. Two ways to get it:

- **From the tree**, by walking it with the split direction in hand: a horizontal split's children are
  left then right, a vertical split's are top then bottom, and an in-order walk that respects that gives
  reading order for every layout that can actually be built. Pure, testable without a window, and it
  never has to wait for a layout pass.
- **From the arranged bounds**, sorting leaves by `(Top, Left)` with a tolerance for rows. Obviously
  correct for any layout, and obviously worse to own: it needs the visual tree, it needs a pass to have
  happened, and "which rows are the same row" is a tolerance somebody has to pick.

The first is the one to write, and the second is what a test can check it against on a handful of real
layouts.

**Renumbering is the hazard, not the numbering.** Every split, close, drag and workspace switch changes
the answer, so the label under a given tile moves. That is correct — it is a *position*, and a position
that did not follow the tile's position would be a name badly spelled — but it means the shortcut a user
learned this morning points somewhere else this afternoon. Worth knowing before building, not worth
solving with pinning: the alternative, an index that never moves, is a second identity to maintain and a
label that lies about where the tile is.

### 2. The shortcut

`Ctrl`+the label focuses that tile. **The modifier must be configurable**, and that is not a courtesy:
`Ctrl+1`…`Ctrl+9` is tab switching in most terminals and in every browser, and this application's whole
purpose is hosting programs that want their own keys. So it is a setting on the General page with a
capture control — `HotkeyCapture` and `HotkeyGesture` already exist for the dictation shortcut and are
the right pieces — plus an explicit "no shortcut" option, offered out loud the way dictation's is.

The handler belongs at window level beside `TerminalClipboardCoordinator`, which is already the place
that decides whether a keystroke belongs to a tile or to the application, and already knows not to
hijack text-editing controls.

### 3. The description

The name field becomes what it should always have been: **the user's own words, empty by default**, with
a watermark saying it can be filled in. What is lost by emptying it — *which tile is this* — is exactly
what the index and the kind glyph now carry, which is what makes the change possible at all rather than
just a blank where a label used to be.

Consequences to settle before touching it:

- **`ITileKind.NameFor` becomes a fallback, not a default.** It generates `Agent#1` and an
  adjective-and-animal for terminals today. Once the index exists, a generated name is a second answer
  to a question something else now answers better.
- **`IFileContent.RenameFile` follows the name**, so a Note's file is named from it. An empty
  description cannot be an empty filename: the note tile needs its own answer before the name can be
  allowed to be blank.
- **Layouts already on disk have names in them.** A name somebody typed is a description and must
  survive; a generated `Agent#3` is not, and carrying it forward would leave every existing tile with a
  description nobody wrote. Telling them apart means comparing against what `NameFor` would have
  produced — which is doable, and is the sort of migration that wants a golden-file test beside the one
  `TileNode` already has.

**Order to build in:** the index and its reading-order rule first (pure, testable, and useful on its own
— it is what makes the shortcut possible), then the shortcut, then the description. The third is the
only one that touches saved layouts, and it is worth having the first two in use before deciding what
the header should look like without a generated name in it.

---

## Agents that are pointed at a provider by name — done, except agy

**Fixed 2026-08-31.** Kept here because the reasoning is the kind that gets undone by somebody tidying.

An instance of opencode or pi pointed at a configured provider used to run silently on the CLI's own
default account. Both were given `OPENAI_BASE_URL` and `OPENAI_API_KEY` and a bare model id — a shape
borrowed from Claude Code, where `ANTHROPIC_BASE_URL` really does redirect. **These two CLIs are not
built that way**: each keeps a registry of providers, decides which one is in play from *which key
variable is set*, and validates `provider/model` against a catalogue before opening a socket. So
`OPENAI_API_KEY` for an OpenRouter instance authenticated against api.openai.com (`opencode auth list`
reports it as the OpenAI provider) and the model was refused outright with `ProviderModelNotFoundError`.

What the fix is made of, and where each piece lives:

- **`IAiProvider.KeyEnvironmentVariable` and `CatalogueId`** — facts about a *service*: what its key is
  called, and what catalogues call it. On the provider because they are the same everywhere the service
  is read; on the agents they would be one table written out five times.
- **`IAiAgent.QualifiedModel`** — the model spelled the way *this* CLI wants it. Claude Code takes the
  id bare and is aimed by address; opencode and pi get `provider/model`. Asked once and used by both the
  tile and the headless goal run, so one instance cannot be spelled two ways.
- **`IAiAgent.SupportsCustomEndpoint`** — whether the CLI can be aimed at a server its registry has
  never heard of. Distinct from `AiProviderCatalog.IsCompatible`, which only asks whether the wire
  formats meet: opencode and pi both speak `/v1/chat/completions` and only one has anywhere to put an
  address.
- **`OpenCodeProviderConfig`** — the generated file that is opencode's only route to a local server,
  written per instance and rewritten every launch, exactly as `OpenCodeSession`'s import document is.
- **`AgentModelResolver`** refuses pi on a local server by name, where it already refuses a deleted
  provider — because until it refuses, the failure is silent.

**What is left:** agy was not measured, and inherits the permissive default (`SupportsCustomEndpoint`
true, model unprefixed). If somebody points an agy instance at a provider, that pairing is a guess.

---

## pi on a local server: allow it when the extension is there

**Today it is refused** (`AgentModelResolver`, `PiAgent.SupportsCustomEndpoint => false`). That is
deliberate and stays until this is built — see *why refusing* below.

### The route exists

`pi-localllm-provider`, a third-party pi extension (`pi install npm:pi-localllm-provider`). Measured
2026-08-31 by installing it and reading its source:

- It calls `pi.registerProvider("localllm-<slug>", { name, baseUrl, apiKey: apiKey || "no-key",
  api: "openai-completions", models: [...] })`.
- `<slug>` is the server's display name lowercased with non-alphanumerics collapsed to `-`, so a server
  called *LM Studio* becomes `localllm-lm-studio`.
- The model is then `localllm-lm-studio/google/gemma-4-12b`.
- Its configuration is `settings.json` → `localllm.servers[]`, each entry
  `{ id, name, baseUrl (ends /v1), apiKey, apiType, models: [{ id, name, contextWindow, maxTokens,
  reasoning, input }] }`. Writable by hand; the `/localllm` TUI wizard is only one way in.

### The blocker, and it shapes the design

```ts
const SETTINGS_FILE = path.join(os.homedir(), ".pi", "agent", "settings.json");
```

The extension reads a **hardcoded path in the home directory and ignores `PI_CODING_AGENT_DIR`**. So the
`OpenCodeProviderConfig` pattern — a generated file per instance — cannot be reused: pi's local servers
are one global list for the machine. Two pi instances cannot point at two different local servers, and
mTiles must not pretend otherwise.

Confirmed the hard way: a `localllm` block written into a temporary `PI_CODING_AGENT_DIR` was never
seen, and pi answered `Model "localllm-lm-studio/…" not found`.

### What to build

1. **Detect** the extension — `pi list` names installed packages, and its own directory is under the
   agent dir. Cheap, and cacheable the way `AiAgentCatalog.Locate` caches a binary for thirty seconds.
2. **Read** `~/.pi/agent/settings.json` → `localllm.servers[]` and match one by `baseUrl` against the
   instance's provider endpoint.
3. **Allow** the pairing when both hold, and have `PiAgent.QualifiedModel` answer
   `localllm-<slug>/<model>` for that server.
4. **Refuse with the way out** when they do not: name `pi install npm:pi-localllm-provider`, and say
   that the server has to be added there — a refusal that points at the fix rather than closing a door.
5. **Do not write that file.** It is global, shared with whatever the user set up by hand, and nothing
   here owns it. Adding a server is the wizard's job.

### Why refusing, and not a warning

pi never fails for want of a provider — it has a default, and it splits `--model` on the first `/`. So
`google/gemma-4-12b`, which is what an LM Studio instance actually stores, is read as **provider
`google`**, ignoring any base URL:

```
Warning: Model "gemma-4-12b" not found for provider "google". Using custom model id.
No API key found for google.
```

That run stopped only because there was no Google key on the machine. With one present it would have
completed **remotely and billed**, while the tile, the row and the header all said LM Studio. Whether
the substitution is noticed depends on whether the user happens to lack a key, which is not a property
anything should rely on — so the tile is refused until the pairing can be checked rather than hoped for.

---

## A CCS provider — Claude Code on a Codex subscription

**What this is.** [CCS](https://github.com/kaitranntt/ccs) (`npm install -g @kaitranntt/ccs`) wraps
**CLIProxyAPI**, a local OAuth proxy (default `http://127.0.0.1:8317`) that serves an
**Anthropic-flavor** endpoint (`/v1/messages`) backed by an OAuth *subscription* — ChatGPT/Codex today,
Gemini, Kimi, xAI and others upstream. Pointed at it, Claude Code runs on a Codex subscription with no
API key anywhere: the proxy owns the OAuth token and refreshes it itself.

**Why a provider and not a new agent.** `ccs codex` as a command is only a wrapper that sets
`ANTHROPIC_BASE_URL` and launches Claude Code — and a provider that could inject CLI fragments would
break the agents' pinned argv tables and session strategies for everybody. The clean seam is the one
that already exists: the CLI stays `claude`, and the provider contributes address (Anthropic flavor),
env (`ANTHROPIC_BASE_URL`/`ANTHROPIC_AUTH_TOKEN`, exactly what `ccs env codex --format anthropic`
exports) and model. `AiProviderCatalog.IsCompatible` then allows the pairing only with `ClaudeAgent`,
which is correct — CCS is a bridge *to* Claude Code.

**What is wrong now.** There is no CCS entry. The stopgap works but is all hand work: pick
**Anthropic**, type `http://127.0.0.1:8317` by hand, run `ccs codex --auth` and `ccs cliproxy start`
in a terminal yourself, and know that the proxy has to be alive before the first launch. Nothing here
detects, starts, or explains any of it — and a dead proxy fails mid-session with a network error
instead of a sentence.

**What to build.**

1. **`CcsProvider`** in `Services/Providers/` — id `ccs`, flavor Anthropic,
   `DefaultBaseUrl` = `http://127.0.0.1:8317`, `NeedsApiKey` false locally (the docker deployment has
   a managed key — then a key field), `IsLocal` true. `ModelsAsync` reads the proxy's
   `/v1/models` (CLIProxy carries a synced catalog). The entry **appears in the Service dropdown
   unconditionally** — LM Studio and Ollama are listed while not running, and hiding an entry somebody
   could otherwise install is the one thing this screen must not do. The form reacts to state instead.
2. **Two buttons on the form, per state** (the agent row's `NOT INSTALLED` chip + **Install…** pattern):
   - **Install CCS**, shown while `ExecutableFinder` does not find `ccs` — runs
     `npm install -g @kaitranntt/ccs` **in a visible terminal tile**, through the `InstallCommand`
     route, never a hidden process.
   - **Auth Codex**, shown while installed and `~/.ccs/cliproxy/auth/codex-*.json` does not exist —
     the sign-in flow: say what will run, then open a tile whose startup script is `ccs codex --auth`
     (`--auth` = auth only, no session). The OAuth consent itself is the one step mTiles cannot do
     silently, and it is one-time; the proxy refreshes the token afterwards on its own. The
     `codex-` prefix of the token file is **inferred from the neighbours' measured naming**
     (`gemini-…`, `kiro-…`, `xai-…`), not measured for codex itself — confirm it against the first
     real login.
3. **A note on the form saying what this is for**: *CCS connects Claude Code to a Codex subscription
   through a local proxy. Only the Codex subscription is wired today.*
4. **Proxy lifecycle** — the one genuinely new member, on an optional interface beside
   `ILocalAiProvider` because no hosted provider can answer it:
   `IManagedAiProvider.EnsureRunningAsync(instance, ct)` — probe the address by protocol
   (`IsServingAsync`, not by port), and when it is down run `ccs cliproxy start` (idempotent), wait
   for health, answer. **Called from `AgentModelResolver.ResolveAsync`** — before any model question,
   because a model list needs the service alive to answer — rather than from
   `TileLauncher.PrepareForLaunchAsync` as this entry first guessed: the resolver is the one place both
   the agent tile and the Goal run already ask before every start, so the ensure reaches both callers
   through code that existed. A failure is the resolver's problem sentence — the tile's
   `LaunchProblem`, the goal's refusal — not a stack trace.
   The `cmd /c` invocation takes its arguments **separately**, never as one pre-quoted string: .NET
   escapes the embedded quotes and cmd answers `not recognized`, exit 1, every time — measured, and
   pinned by a test that runs a real shim.
5. **Context window.** The model behind the proxy is `gpt-5.x` — an id Claude Code does not know, so
   it assumes 200 000. `ContextWindowAsync` answers from the proxy catalog where it can; the
   instance's own `MaxContextTokens`/Auto-compact fields (Claude Code only) are the manual answer and
   need no new code.

**Later, deliberately not now — more subscriptions.** The point of CCS is that CLIProxy speaks to
*many* OAuth subscriptions. When a second one is wanted, choosing CCS on the form should grow a
**subscription choice** (Codex / Gemini / Kimi / …), each with its own auth flow (`ccs <provider>
--auth`), its own token directory under `~/.ccs/cliproxy/auth/`, its own model spellings and windows.
Nothing in the shape above blocks it — the provider stays one, the choice is an instance field — but
each subscription is a measured integration of its own, and one that works should ship before a
chooser promises five.

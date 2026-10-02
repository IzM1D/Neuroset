# Neuroset

## Веб-версия для GitHub Pages

Текстовый веб-чат с OpenAI-совместимым `/chat/completions` находится в **[`apps/web`](apps/web/README.md)**: интерфейс в стиле ChatGPT, потоковые ответы, история чатов в браузере, Markdown, подсветка кода и настройки API.

```powershell
cd apps/web
npm ci
npm run dev
```

Для публикации выберите **Settings → Pages → Source → GitHub Actions**. Workflow **Web · GitHub Pages** соберёт и опубликует сайт после отправки изменений в основную ветку. [Подключение API, ограничения CORS и инструкция](apps/web/README.md).

---

Neuroset is a local-first Windows and Android client for OpenAI-compatible LLMs and a permission-controlled AI agent. This monorepo shares the XAML interface, view models and .NET libraries between native WinUI 3 on Windows and Uno Platform on Android. Conversations, settings, memory, schedules and knowledge indexes are stored locally in SQLite.

See [Windows and Android build instructions and platform behavior](docs/cross-platform.md).

## Features

- Multiple OpenAI-compatible connections with arbitrary base URLs, streaming, non-streaming responses, tool calls, cancellation, retries, timeouts, model discovery, and manual model entry.
- Global and per-chat generation profiles with system prompts, directive presets, temperature, top-p, output-token limits, presence/frequency penalties, deterministic seeds, stop sequences, and tool availability. Optional research modules add deterministic input perturbation, regex-based response cleanup, context-adaptive sampling, and parallel scoring across distinct models configured as saved connections. A per-chat profile replaces the global profile instead of merging with it.
- API keys protected with Windows DPAPI or Android Keystore and stored separately from SQLite, with optional backup keys per connection and automatic failover on quota or credential errors.
- Independent agent loop with separate Chat/Coding budgets, structured events, execution history, permissions, an explicit untrusted-input boundary, adaptive work plans, specialized coding subagents, and deterministic long-context compaction. Coding chats default to 256 iterations and a 120-minute overall timeout.
- Native calculator, unit/currency, weather, world-time, and live-sports widgets; Web Search, Web Fetch, SSRF-protected HTTP/API requests, local PDF/DOCX/TXT/MD/CSV/JSON RAG, MiniLM ONNX embeddings, workspace file operations, sandboxed Python execution, immediate interactive HTML visualization, direct process execution for full-access Coding chats, isolated user database, short/long-term memory, scheduler, and Windows notifications.
- A provider-independent Model Reliability suite for benign instruction-hierarchy, encoded-input, refusal-calibration, and assistant-prefill compatibility checks, with a local Markdown report.
- Persistent chat history, per-conversation system prompts, message editing and regeneration, Markdown, syntax highlighting, code copying, attachments, drag-and-drop, message/conversation search, pinning, renaming, and deletion. Sending is disabled while a response is still generating.
- Coding-agent project guidance compatible with repository `AGENTS.md`, `CLAUDE.md`, and `SKILL.md` workflows; project MCP servers from `.mcp.json` or `.neuroset/mcp.json`; atomic verified text patches; structured Git status/diff/log inspection; recoverable dirty-worktree checkpoints; managed Git worktrees; and a fixed semantic color palette for model-authored Coding status text.
- Eight live themes plus accent, fonts, scale, sidebar, density, radius, transparency, and animation controls.

## Backup API keys

In **Settings → Connections → Backup API keys (optional)**, choose **Add backup API key**
for each additional key from the same provider, then **Save**. Saved keys are masked;
leave a saved field blank to keep it, enter a new value to replace it, or delete its row
and save to remove it. Existing connections work without any backup keys.

Each request starts with the main key. On quota, rate-limit, billing or authentication
errors (HTTP 401/402/403/429, or recognized provider error codes), the app tries the
remaining keys in order. It resends the same serialized body, including messages,
attachments, tools and generation parameters; only the authorization key changes.
JSON errors returned with HTTP 200 and errors before the first SSE response data also
support failover. Normal model text is never interpreted as an API error. Once response
text, tool-call data, usage or a finish marker arrives, a stream is not replayed.

Keys are tried once each for these errors; duplicates and unavailable secrets are skipped.
When all keys fail, the last error is shown. Cancellation and the connection timeout cover
the whole sequence. Model discovery uses the same key order. Image requests can switch keys
after an explicit quota/credential rejection; timeouts and ambiguous server failures do not
trigger another image generation. All key values remain in the encrypted secret store;
SQLite holds only their references.

The editor and encrypted key lifecycle can be tested on Windows with
`./scripts/test-android-layout.ps1 -ConnectionKeys` using an isolated test database.

## Image generation

The composer shows an icon-only image toggle when the active connection's `/models`
endpoint lists `gpt-image-2` or another GPT Image family model (including provider-prefixed
IDs). Discovery runs at startup and when the saved connection changes. Vision-only
chat models do not enable the button. GPT Image 2 is preferred when listed.

Press the image button, enter a request, and send. The toggle applies to that message
and resets after sending or switching conversations/connections. Without it,
`image_generation` is absent from the model's tools and unsolicited calls are rejected.
An explicit image request enables just this tool even if general tools are disabled;
roleplay continues to run without tools.

The selected conversational model receives a forced function call and rewrites the
request into a technical prompt with positive `{filter}` and negative `-{filter}`
visual constraints. These constraints are explained in the prompt; they are not
separate GPT Image API parameters. The tool calls `/images/generations` on the same
connection and requests one PNG. See the [official OpenAI image generation guide](https://developers.openai.com/api/docs/guides/image-generation)
and [GPT Image 2 model documentation](https://developers.openai.com/api/docs/models/gpt-image-2).

Generated images appear inside the response with open/save actions and remain available
after reopening the chat. Files live under `%LOCALAPPDATA%\Neuroset\Artifacts\<conversation-id>`.
Only one image tool call is allowed per image-enabled message. Configured backup keys
can be tried after an explicit quota/credential rejection; other API errors and
timeouts do not automatically trigger additional paid generations. Regenerating a
response requires selecting the image toggle again to authorize a new image request.
Providers must support Chat Completions function calling with forced `tool_choice`
and GPT Image's base64 PNG response format.

## Animated presentations

Ask the chat to create a presentation, for example: **«Сделай презентацию про
космос на 10 слайдов в PPTX»**. The `presentation` tool uses the active chat model
to plan and write the deck, selects a coordinated visual style, downloads real
web images or generates illustrations through the connected GPT Image API according
to the user's request, and returns an editable file
with **View and edit** and **Save presentation** actions. The image composer
toggle is not needed for presentation illustrations. Normal tool permissions and
the active profile's tool setting still apply; Roleplay has no tools.

- Supports 1–40 slides, including the opening and closing slides. An explicit
  count is checked before export; longer decks are written in small batches.
- Uses `.pptx` by default for creation and saving. Exports native `.pptx` and `.odp`;
  the model cannot change the default unless the user asks for another format.
  Requests for ODF use the standard `.odp`
  presentation extension. Office, LibreOffice, Python and Node are not required
  to create or view Neuroset-generated decks in the app.
- **View and edit** opens a native viewer with slide thumbnails and step-by-step
  animation playback. Edit titles, paragraphs, notes, layouts, fonts, colors,
  themes and animations with a live preview. Space reveals the next animated
  block; arrow keys navigate slides. External PPTX/ODP import is not supported.
- Manual edits save a revision of the same deck without calling a model or
  regenerating images. Old chat cards reopen and save the latest committed
  version, and subsequent AI edits build on it. Unsaved edits survive slide
  navigation; invalid/overflowing text blocks saving. Concurrent revisions cannot
  silently overwrite each other. **Save presentation** defaults to PowerPoint
  and also offers OpenDocument; format-only export does not create a revision.
- Includes six built-in themes, custom palettes and supported fonts, six slide
  compositions, large editable text, speaker notes and embedded PNG illustrations.
  Text fit and element bounds/overlap are checked. PPTX packages are validated
  with the Open XML SDK before delivery.
- Supports up to four images per slide and a photo gallery layout. Web photographs,
  maps, flags and coats of arms come from public sources, with source URLs and
  attribution in notes. Maps and symbols can be shown in full without cropping.
  Chat research is passed to the slide writer; web images do not need a GPT Image
  model and are never silently replaced with generated illustrations.
- Long image queries are simplified without rewriting the slide. Wikimedia requests
  are paced and respect rate limits; Openverse provides an independent image search
  fallback. Automatic results must match the subject and image type. Stock previews
  and unrelated search pages are excluded; recently forbidden/missing URLs are skipped.
- Writes native fade transitions and fade/wipe entrance effects. Titles and
  the first image appear automatically; body blocks and additional images reveal on clicks. Explicit
  requests for no animations or no images are respected. Playback varies by viewer.

For a correction, say **«На слайде 3 измени только заголовок на …»** or
**«Замени изображение на слайде 2»**. The existing deck keeps its ID. A separate
model pass first identifies the smallest requested scope, then a constrained
writer proposes changes only to those fields. Scope, original values and revision
are checked in code; slide count and order are immutable. Unrequested text,
images, styles, notes and animation settings are preserved. Ambiguous feedback
produces a clarification without changing the deck. Corrections and their scope
are retained with the document for later revisions. Local text/color/font/layout
edits and explicit deck-wide theme edits are supported.

Documents, generated assets, feedback and downloadable revision snapshots live
under `%LOCALAPPDATA%\Neuroset\Artifacts\<conversation-id>\presentations`.
The latest committed revision is updated atomically only after successful export.
Failed image/export work saves a pending draft after each completed asset. The
agent can correct scope/patch validation errors or resume the draft in the same
turn, with up to three mutation attempts and reuse of completed assets. A later
**«Продолжи сохранённую презентацию»** also resumes that draft. Text reduction can
remove excess body paragraphs while keeping notes and titles unchanged. Replacing
one image preserves every other image. If generation is requested but the API has
no GPT Image model, the draft remains available until a suitable connection is
selected. Text edits and format-only exports reuse existing images. A failed web
download does not stop the remaining assets. The report lists `pendingImages` and
the completed image count. When a source remains unavailable, `resume` accepts
`image_sources: [{slide_number, image_number, url}]` for unresolved web assets only;
it cannot change their subjects, completed images, slide text, layout or animation.

The tool operates on presentations created in Neuroset; arbitrary external deck
import and slide insertion/deletion are not part of this editing workflow.

## Architecture

```text
Windows host / Android host -> Shared XAML and view models
                            -> Application -> Agent -> Providers / Tools
                                           -> Storage (SQLite) / Security
                            -> Platform adapters (keys, files, images, notifications)
```

Application entry points live in `apps/windows` and `apps/android`. Shared screens remain in `src/App`; shared libraries are under `src/Domain`, `Application`, `Providers`, `Agent`, `Tools`, `Storage`, `Security` and `Infrastructure`. Windows services live in `src/Platform.Windows`. Portable tests live in `tests/Neuroset.Tests`.

## Requirements

- Windows 10 version 2004 (build 19041) or newer, or Android 8.0 (API 26) or newer.
- .NET SDK 10.0.400 (pinned by `global.json`).
- Android development: .NET `android` workload, JDK 17, Android SDK platform 36 and build-tools 36.0.0.
- The supplied unpackaged and MSIX release workflows are self-contained and include their .NET/Windows App SDK runtime files.
- Python 3 for the `run_python` tool. Full-access Coding chats can also invoke executables directly through `run_process`; the application passes an explicit argument array and does not insert a command shell automatically.

## Build and test

```powershell
.\scripts\build.ps1 -Configuration Debug
```

Build Android or just the portable core:

```powershell
.\scripts\build.ps1 -Platform Android
.\scripts\build.ps1 -Platform Core
.\scripts\publish-android.ps1 -Runtime android-arm64
```

The APK is written to `artifacts/Release/android-arm64`. It uses the local
development signing key for sideloading. The full solution is `Neuroset.slnx`;
`Neuroset.Core.slnx` builds and tests without platform workloads. Android Coding
operates within the app sandbox; Python, Git and external process runtimes are
not bundled. See the platform behavior table linked above for these OS differences.

The real MiniLM download/inference and local indexing integration tests are opt-in:

```powershell
.\scripts\build.ps1 -Configuration Debug -RunModelTests
```

## Run an unpackaged build

```powershell
.\scripts\publish-unpackaged.ps1 -Configuration Release -Runtime win-x64
.\artifacts\Release\unpackaged-win-x64\Neuroset.App.exe
```

## Build a signed MSIX

For a local development package, create a user-scoped development certificate:

```powershell
.\scripts\package-msix.ps1 -Runtime win-x64 -CreateDeveloperCertificate
```

Windows requires an interactive confirmation before trusting a self-signed root. Double-click `artifacts\certificates\Neuroset.Development.cer`, choose **Current User**, place it in **Trusted Root Certification Authorities**, and then install:

```powershell
.\scripts\install-msix.ps1 -Runtime win-x64
```

For distribution, pass a trusted code-signing PFX whose subject matches `CN=Neuroset`:

```powershell
.\scripts\package-msix.ps1 -Runtime win-x64 -CertificatePath C:\secure\Neuroset.pfx
```

Do not commit generated PFX files. The local development certificate is only suitable for sideloading on machines where its public certificate is trusted.

Clean package uninstall preserves the local database and user documents intentionally:

```powershell
.\scripts\install-msix.ps1 -Runtime win-x64 -Uninstall
```

## Local data and security

Application data is stored below `%LOCALAPPDATA%\Neuroset`:

- `neuroset.db` — conversations, messages, settings, permissions, memory, schedules, and vector metadata;
- `secrets.dat` — encrypted API key entries (DPAPI on Windows, Android Keystore on Android);
- `Attachments` — application-owned copies of user attachments;
- `Models` — locally downloaded ONNX embedding models;
- `Sandbox` — isolated Python working directories;
- `Artifacts` — standalone interactive HTML simulations and visualizations created by the model;
- `Logs` — structured logs with a 14-day retention policy.

Web pages, documents, and tool results are treated as untrusted contextual data. Mutating HTTP calls, code execution, database writes, and scheduler changes pass through the centralized permission service. API keys are never injected into tools or written to logs.

## Branding

`build/generate_assets.py` regenerates all Windows/MSIX images from the project-owned `NeurosetMark` design using Pillow.

## Web search recovery

`web_search` gives each search provider 10 seconds and the complete search call
25 seconds. DuckDuckGo failures, timeouts, and bot checks fall back to Bing RSS.
Failures return a structured report with provider diagnostics to both the model
and the tool journal. `fetch_url` has a 30-second deadline, including the response body.

After five unsuccessful search calls (including empty results) in one response,
the runtime closes queued calls and requests a final answer with tools disabled.
Successful results remain available for that answer. The model can retry earlier
or finish with partial evidence; user cancellation still stops the response.
Interrupted model streams also produce tool errors instead of leaving calls in
`Preparing`, and interrupted reports are preserved when the chat is reloaded.

## Python and interactive HTML tools

The model can call `run_python` for calculations, data preparation, and local
experiments. Normal chats use an isolated Python process with blocked network,
subprocess, registry, and out-of-workspace file access. Explicit full-access
Coding chats use the selected workspace and user-level process permissions.

The `render_html` tool displays model-authored HTML/CSS/JavaScript immediately
inside the current assistant message and keeps a persistent copy under
`%LOCALAPPDATA%\Neuroset\Artifacts\<conversation-id>`. A restrictive Content
Security Policy and navigation guard block external network resources while
allowing inline code for interactive simulations, charts, and educational models.

## Model reliability evaluation

Open **Settings → Model reliability** to run isolated checks against the active connection. The suite uses the same provider factory as normal conversations, so it works with any configured OpenAI-compatible Base URL rather than checking for OpenRouter or localhost.

The built-in probes use harmless markers to measure system/user instruction hierarchy, Base64 parsing robustness, over-refusal and explicit-boundary compliance, and trailing assistant-prefill compatibility. Results remain local and are rendered as a copyable Markdown report. The evaluation pipeline does not rewrite ordinary chat messages or automatically retry requests to defeat a provider refusal.

## Python evaluation kit

`python/neuroset_eval` adds a standalone, dependency-free evaluation and A/B
testing CLI for OpenAI-compatible `/chat/completions` endpoints. It supports an
empty editable global system prompt, reusable prompt profiles, messages of all
roles, multiple models and parameter variants, refusal/quality/relevance scores,
isolated assistant-prefill compatibility tests, exact request previews, approval
hashes, per-request audit records, and JSON/CSV/Markdown export.

The kit is deliberately transparent: transforms always produce a before/after
diff, assistant-prefill is rejected outside an explicitly isolated case, and
model refusals never trigger automatic rephrasing or bypass retries. See
[`python/README.md`](python/README.md) and
[`python/examples/evaluation-set.json`](python/examples/evaluation-set.json).

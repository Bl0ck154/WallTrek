# Personalized Daily Wallpapers — Fork Roadmap

Status: **development implementation integrated into main on 2026-10-16**, via [PR #4](https://github.com/Bl0ck154/WallTrek/pull/4). Automated Windows tests and WinUI build passed on integration head `dbc3faaf`. **Not a validated release**; see checklist below.
Repository: [Bl0ck154/WallTrek](https://github.com/Bl0ck154/WallTrek), fork of [swax/WallTrek](https://github.com/swax/WallTrek) (MIT).
Read `CLAUDE.md`, `docs/CODEX_INTEGRATION.md`, and `docs/PROVIDER_ARCHITECTURE.md` before development.

## Implementation checkpoint (2026-10-16)

- **Integrated into main, automated tests green:** custom OpenAI-compatible API provider profiles, encrypted per-user credentials, reference-photo library, scenario editor/no-repeat variations, provenance/history, and **opt-in experimental** Codex subprocess adapter (prompt-only from normal wallpaper screen; image editing is available only through separate bridge/Settings probe).
- **Not complete:** linking the Codex helper's edit with the personalized scenario/photo flow ([#5](https://github.com/Bl0ck154/WallTrek/issues/5)); durable once-per-day local-time scheduling across reboot/sleep/DST ([#6](https://github.com/Bl0ck154/WallTrek/issues/6)); consented live Windows API/Codex image tests, UI smoke tests, secret migration and account-terms review before shipping ([#7](https://github.com/Bl0ck154/WallTrek/issues/7)).
- **Do not describe this as released or Codex generation as proven.** CI uses fake/mock provider/helper tests and Windows compilation. No actual eligible Codex account or third-party reference edit has been tested. The experimental Codex choice is disabled by default and must remain opt-in.

## Product goal

A Windows desktop application that, after a one-time setup, **automatically creates a new wallpaper every day**, using (a) one or more personal reference photos, (b) a user-authored scenario or rotating scenario collection, and (c) a chosen image-generation backend. It archives each result, can manually regenerate/restore images, and optionally updates the desktop wallpaper.

The **primary differentiated flow** is “this recognizable person appears in a new place/situation every day,” not just prompt-only random backgrounds. The user can bring a provider of their choice, including an OpenAI-compatible API with a **custom host/base URL**, or **experimentally** an already signed-in Codex/ChatGPT account via a locally installed helper. No app-operated backend or paid API key is required for the Codex experiment; success/eligibility and account limits are *not guaranteed*.

## Current upstream features to keep working

- Windows 10/11, .NET 10, WinUI 3, system tray and optional autostart.
- OpenAI and Google image backends, Anthropic/OpenAI prompt generation, model selection/cost information.
- Scheduled generation, Windows wallpaper setting, history/search/favorites, SQLite, local file storage.
- Optional upscale, DeviantArt and video paths: **preserve**, but defer any redesign. Do not break them.
- Keep MIT attribution and upstream link; make changes in this fork only.

## Target user journey

1. Install/open app, optionally start with Windows, select a display/monitor (initially keep upstream single-wallpaper behavior).
2. Choose generation provider: **existing** OpenAI/Google, **custom OpenAI-compatible endpoint**, or **Codex (experimental)**. Show real eligibility/test result; do not invent model availability or pricing.
3. Add a reference library: 1–5 local images, role (identity/content/style), optional primary portrait, active set, enable/disable, preview, remove, manage storage. Explicitly inform user which provider will receive them.
4. Define a persistent scenario (“Keep my facial identity; place me in a different European city daily, photorealistic, landscape”), optional style, forbidden elements, shuffle variables and exclusions. Preview final resolved prompt.
5. Pick a daily local time, output resolution/aspect ratio and wallpaper fit mode, automatic or preview-before-apply, and a policy for missed runs.
6. Generate a **test wallpaper**. See detailed provider failure reasons. Confirm the result and turn on daily schedule.
7. View image archive, original references/scenario/prompt/model/provider metadata, favorite/regenerate, restore to desktop, delete locally.

## Non-goals for initial MVP

- macOS/Linux, hosted user accounts, central server, subscription/paywall, community/gallery sharing changes.
- Scraping browser ChatGPT UI, bypassing Codex/account rate limits, hard-coding ChatGPT cookies or OAuth refresh flows into WallTrek.
- Promising exact facial identity consistency (best effort; evaluate with repeat samples).
- Automatic fallback that silently uploads private reference photos to another vendor.

## Architecture changes

### A. Versioned request/result contract

Current `Services/ImageGen/IImageGenerationService.cs` has `GenerateImage(string prompt)`. Introduce a typed `ImageGenerationRequest` (text, reference attachments and role, desired size/aspect, seed when supported, provider/model selection, correlation ID, cancellation, optional output quality) and `ImageGenerationResult` (bytes, format, actual dimensions, provider/model, latency, warnings, safe metadata). Reference data is explicitly opt-in per run. Add capabilities describing text-to-image, image-to-image/edit, multiple references, size support, etc. Unsupported options fail visibly rather than being silently dropped.

Provide a registry/factory that resolves enabled provider **profiles**, with first-party OpenAI/Google adapters migrated to the new contract. Reuse upstream history and file-saving paths. Separate image generation from optional LLM scenario/prompt generation, with independent model selection.

### B. Multiple providers and custom hosts

Use the profile architecture from `docs/PROVIDER_ARCHITECTURE.md`. Users can add/edit/duplicate/delete named OpenAI-compatible profiles with **base URL**, API key or no-auth selection (for explicit local servers), optional allowed headers, text/image model names, and endpoint modes. Show test connection and image edit/generate capability results. Some compatible chat APIs do **not** support image generation. Never represent a text-only model as an image generator.

### C. Codex connected account (experimental)

Use `docs/CODEX_INTEGRATION.md`. Detect an installed Codex CLI and its session **without copying tokens**. Investigate a supported Codex CLI/SDK or app-server image-output flow first. If this cannot deterministically return image bytes/files, prototype an **explicit opt-in, audited, local** `codex-imagegen-cli` subprocess integration, with conspicuous experimental/unsupported labeling. Codex is **not** equivalent to the public Images API and included account usage/availability vary. Missing helper, expired login, rate limit, changed endpoint and unsupported image tooling must be graceful states.

### D. References, scenes, and identity consistency

Create local `ReferenceSet` records and copy-on-import to an app-controlled reference directory (avoid depending on removable originals). Track consent/active status and attachment role. Allow a main person reference and optional style reference(s). Strip accidental EXIF/geolocation on outbound images where possible; preserve originals locally. Resize/upload exact chosen inputs with previews and enforce provider attachment/size limits.

Create a `Scenario` entity with template text, variable pools, “avoid repeat” memory, style constraints and optional calendar-aware variables. Make deterministic preview of final resolved prompt and explicit safety/identity clauses. Persist used scenario variable values and prompt with each generation. Do not make the LLM rewrite the user’s identity constraints unexpectedly.

### E. Scheduling, wallpapers and history

Extend `Services/AutoGenerateService.cs`: daily **wall-clock local-time** mode (plus current interval mode), missed-run policy, DST/timezone changes, sleep/resume, single-flight lock, idempotency per scheduled slot, cancellation, visible next run and bounded retry/backoff. The current one-second UI timer can remain UI-facing; do not trust it as the sole long-running scheduler. Persist job state and use OS task scheduling or equivalent robust wake/restart strategy after evaluation.

Successful render flow: validate image bytes -> atomic save + database record -> optional upscale/fit -> set wallpaper -> mark success. Failed/cancelled render **must leave the prior wallpaper in place**. Support cover/contain/crop positioning and preview for common 16:9/16:10/ultrawide resolutions. Keep originals, avoid destructive resizing.

### F. Security/privacy

- Never put Codex tokens, API keys, photos, or generated binary content in Git, telemetry, crash logs, or diagnostics.
- Migrate plaintext provider credentials from `%APPDATA%\WallTrek\settings.json` to Windows Credential Manager or equivalent DPAPI-protected per-user store; leave only opaque key references in JSON. Migrate existing settings backwards-compatibly and test rollback.
- Show destination host + consent before sending reference images to a newly selected provider or changing fallback provider.
- Use HTTPS for remote providers; allow explicitly approved loopback HTTP for local models. Validate URL/redirects and never forward a key to a different origin.
- Bound file size, concurrency, timeouts, retries, output paths; redact all sensitive strings in logging.
- Use local-only storage by default; user can delete imported originals and archive entries independently.

## Existing files / likely change points

- `Services/ImageGen/IImageGenerationService.cs`: typed request + capabilities/results.
- `Services/ImageGen/ImageGenerationServiceFactory.cs`, `ImageModelCatalog.cs`: provider profile registry + dynamic models.
- `Services/ImageGen/OpenAiImageGenerator.cs`, `GoogleImagenService.cs`: adapt and add reference/edit support when provider supports it.
- `Services/Settings.cs`: profile configuration, reference/scenario/schedule IDs, settings migration and credential references.
- `Views/SettingsView.xaml[.cs]`: provider manager, custom URL, secret entry, Codex detection/status, test button.
- `Views/MainView.xaml[.cs]`: scenario and reference selection, preview/regenerate, capable-model picker.
- `Services/AutoGenerateService.cs`, `App.xaml.cs`: durable scheduled execution and recovery.
- `Services/DatabaseService.cs`, `FileService.cs`, history/image views: new schema, provenance and archival operations.

Use small services, avoid growing `MainView.xaml.cs` (already large), keep UI-independent domain/provider logic unit-testable.

## Incremental delivery plan / agent-sized PRs

| Step | Deliverable | Dependencies | Done when |
|---|---|---|---|
| 0 | Baseline audit, Windows build, test harness and architecture ADR | none | Unchanged fork builds on Windows; main flows smoke-tested; migration baseline documented |
| 1A | Codex viability spike (isolated) | 0 | Logged-in Windows Codex can/cannot produce image file from scripted call; generation+edit tried; permissions/limitations documented; no credentials exposed |
| 1B | Request/result types, capabilities, provider registry | 0 | Existing OpenAI/Google behavior preserved; fake adapters tested; no UI behavior change |
| 2 | Custom OpenAI-compatible provider profiles + secure secrets | 1B | User enters host/key/model; test connection; generation and image-edit feature probe; incorrect endpoints fail clearly; old keys migrated |
| 3 | Reference library + edit pipeline | 1B, 2 | 1–5 refs can be imported/removed; identity/style roles; supported providers consume them; unsupported ones explain why |
| 4 | Scenario templates + recurrence exclusions | 3 | Save/select scenarios; deterministic prompt preview; per-run resolved variables/provenance recorded |
| 5 | Codex adapter behind feature flag | 1A, 1B, 3 | Opt-in path works in tested environment or remains explicitly unavailable; no raw tokens in app; no unsafe default backend |
| 6 | Daily reliable scheduling + wallpaper fit | 3, 4 | Missed runs handled once, sleep/restart/DST tests, no overlapping generations, failure preserves wallpaper |
| 7 | UX polish, privacy audit, Windows installer/release | 2–6 | First-run wizard, accessible consistent settings, docs, Windows smoke test, signed/publish approach decided |

**Parallelization:** agent A can handle Codex feasibility (1A), agent B provider contract/registry (1B), agent C scenario/reference UX research and SQLite migration design (no overlapping writes). Merge contracts before implementing dependent changes. Do not let parallel agents modify the same UI files.

## Acceptance checks for MVP

- A nontechnical Windows user completes first run without editing source/config files.
- Custom profile against at least one real OpenAI-compatible third-party service works for image generation; editing tested separately and correctly marked unsupported if necessary.
- A reference portrait produces a recognizably related new scene in supported provider; negative test ensures text-only provider refuses reference rather than ignoring it.
- User-defined scenario rotates at least 7 runs without unintended duplicate variable combinations; manual regenerate works.
- Daily wallpaper generation continues after a reboot; only one result per due slot even with restart/sleep. Failure leaves old background intact.
- Imported private photos/API keys never appear in logs or repo; API host cannot silently change after consent; secrets survive normal app restart.
- Codex setup wizard reports **detected, signed in, image-capable, not-capable, rate-limited, unsupported** distinctly; experimental risks disclosed.
- Existing provider paths, optional upscale, favorites/history and tray still work (regression tests).

## Execution policy and open decisions

**Do not implement the full product in one mega-PR.** Each feature should include a Windows-verifiable test/evidence, migration plan, rollback note and user-visible error handling. Do not run Windows builds on production Linux hosts. No hidden CI builds on docs-only changes.

Resolve in step 0–1: whether Codex CLI/SDK exposes a supported scripted image artifact; whether a third-party helper may be offered under applicable policies; exact secure-credential mechanism; reference file retention/EXIF default; task-scheduler vs app-resident scheduling; initial Windows packaging strategy.

### References

- [Upstream WallTrek](https://github.com/swax/WallTrek)
- [Codex official image generation tool source](https://github.com/openai/codex/blob/main/codex-rs/ext/image-generation/src/tool.rs)
- [Unofficial codex-imagegen-cli](https://github.com/jdmnk/codex-imagegen-cli) (alpha; **not an OpenAI-supported API**).

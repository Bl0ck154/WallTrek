# Universal provider implementation record — 2026-10-16

Status: **Draft, pending validation**, PR #2. This document records implementation behavior and release gates; it does not imply compatibility with all OpenAI-compatible servers.

## User workflow

Settings → **Providers** → New / Duplicate / Delete. Configure provider name, Base URL (including `/v1` when required), authentication (Bearer / `x-api-key` / no-auth **for explicit local loopback only**), generation model, optional edit model, optional text model metadata, and relative endpoint paths. The default image paths are `images/generations` and `images/edits`, model probe path `models`. Edit multipart input shape is `image` or `image[]`. Save profile, then select its model in the existing wallpaper image dropdown. There is **no hard-coded cap** on number of profiles.

API key is masked and blank when switching profiles; leaving it blank on edit retains the existing key. Duplicate creates a fresh GUID and requires its own API key. Keys are stored with **Windows DPAPI CurrentUser** at `%LOCALAPPDATA%/WallTrek/Secrets`, identified by opaque profile references; `providers.json` contains only non-secret config. Other Windows users cannot decrypt the key using DPAPI.

**Connection** calls `GET {base}/models`. This tests the host/credential, **not** image capability. Generate and Edit are independent, consented live smoke tests that may bill an account; the edit test requires a user-specified disposable reference image and shows destination origin before uploading. A successful test records verification timestamp, but does not promise future endpoint availability or facial identity preservation.

Production HTTP requests disable automatic redirects and cookie forwarding, reject non-HTTPS origins except explicit loopback HTTP, reject userinfo/base query/fragment, restrict endpoint paths to relative safe segments, send credentials solely to configured origin, reject cross-origin image URLs, and issue unauthenticated same-origin image GETs. Inputs/outputs have size/dimension limits and cancellation paths. No silent switch to another provider, credential read from Codex, account/terms bypass, free-price claim, or retries that could incur double billing.

Unsupported **at this stage**: provider-specific arbitrary headers, fully custom request schemas, separate remote text/LLM inference (the optional model ID is preserved as metadata only), automatic multi-reference daily scenario UI, external signed CDN URLs (blocked by default to avoid unapproved data flows), and WebP wallpaper output conversion (PNG/JPEG work through current FileService). Not every `chat/completions` provider exposes `images/generations` or `images/edits`. Some providers require extra vendor-specific parameters or return URLs from separate media hosts; these will need future declared adapters and explicit host consent.

## Legacy secrets migration / rollback

On the first upgraded `Settings.Load`, any plaintext legacy OpenAI, Anthropic, Google, Stability, fal.ai and DeviantArt values are written to per-user encrypted DPAPI storage. **Only after all writes succeed** does WallTrek null legacy JSON values and atomically rewrite `settings.json`. If migration cannot protect a key, do not rewrite source JSON, and surface an error. The previous JSON should remain recoverable. The existing OpenAI/Google/Anthropic call paths continue using the unchanged `Settings.Instance.*ApiKey` properties, now backed by the vault.

**Rollback warning:** after migration, a build older than this feature may not read API keys from the encrypted store. Before reverting, users must separately re-enter keys into the old version or retain the new DPAPI-backed build. Never export decrypted keys into a diagnostic archive. Keys are tied to the same Windows user profile and may be lost on profile reset without backup/re-entry.

Deleting a custom profile removes its encrypted key entry and settings, not previously generated wallpaper history.

## Required manual verification (Windows)

1. Run `dotnet run --project tests/WallTrek.Provider.Tests/WallTrek.Provider.Tests.csproj` on isolated Windows runner (fake endpoints, no actual keys).
2. Build WinUI: `dotnet build WallTrek.csproj -c Debug`. Open the app, add/edit/duplicate/remove 2+ profiles. Verify existing Google/OpenAI options persist and still generate.
3. Verify original plaintext credentials migrate and disappear from settings.json after successful DPAPI write, restart retains them, corrupted vault fails with explicit error; failure before write leaves original JSON intact.
4. Test HTTPS, local loopback HTTP opt-in and refused remote HTTP; 401, 403, 404, 429/Retry-After, 500, invalid image, oversized response, hanging endpoint timeout, cancellation, and redirect/cross-origin URL refusal without leaking authorization.
5. Real non-OpenAI provider: `GET /models`, then actual generation with returned PNG/JPEG (record exact route/model), then actual edit with 1–5 explicitly consented dummy photos (verify shape and correct content usage). If unsupported, mark **unsupported**, not “working.”
6. Confirm reference images and secret strings never appear in Settings JSON, crash logs or provider HTTP redirects; check user-visible destination before an edit; no fallback on failure.
7. Merge after PR #1: keep Codex feature flag and settings fields; add its model alongside custom profiles; do not replace agent 1's factory branch. The shared new `IImageProvider` stays in `Services/Providers`; the existing interface `Services/ImageGen/IImageGenerationService.cs` is unchanged.

## Source contracts

- Official OpenAI generation: https://developers.openai.com/api/reference/resources/images/methods/generate
- Official OpenAI edit: https://developers.openai.com/api/reference/resources/images/methods/edit
- User-scope Windows DPAPI: https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope

This implementation is an OpenAI-compatible **adapter**, not a claim that all third-party endpoints support image generation or editing.

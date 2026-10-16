# Provider Profiles and Custom OpenAI-Compatible Hosts

Status: **architecture proposal**; not implemented. See [ROADMAP.md](ROADMAP.md) for delivery steps.

## Scope

Let the user add **any explicitly chosen API service**, including an OpenAI-compatible service at a different base URL (for example a company gateway, a third-party AI router, a locally hosted server, or the ordinary OpenAI API). Do not restrict providers to a closed hard-coded list.

This is **not** the same as guaranteeing that every OpenAI-style `/chat/completions` server exposes image generation or edits. Show the supported feature set per provider and model.

## Separate profiles, models and tasks

Provider profile (one user-configured connection):
- Stable UUID; user-visible display name; adapter kind: `OpenAiCompatible`, built-in `OpenAI`, built-in `Google`, or `CodexExperimental`.
- Base URL, generation route, optional edit route and chat-completions route, each using the user's explicitly selected routing convention. Store sanitized URL and endpoint mode, not raw sensitive query params.
- Credential reference in Windows Credential Manager/DPAPI vault; authentication mode (`Bearer`, an explicitly requested header mode, or none for loopback). Optional static headers without secrets; secret headers go in the credential store.
- Separate model IDs for **image generate**, **image edit**, **text/LLM**. Permit manually entered IDs and optional API-discovered list, since proxy model names can be nonstandard. Store provider capabilities per model with verification timestamps.
- Optional extra parameters/headers with validation, maximum size and redaction; no arbitrary user-script execution.
- Timeouts (longer for images than chat), bounded retries/backoff, maximum image byte/dimension limits, known pricing if verified; otherwise `unknown`. Never hardcode free or guessed prices.

User-facing add-provider form:
1. Name and adapter (`OpenAI-compatible` preselected for custom hosts).
2. Base URL (e.g., `https://gateway.example.com/v1`), auth method and key entered in a masked field.
3. Model(s) (editable text, not fixed dropdown), operation paths/modes as advanced settings.
4. `Test connection` (basic auth/list-models if available), plus distinct `Test image generation` / `Test image edit with reference`; show model capability and safely redacted error detail.
5. Save and select independently for image generation vs optional LLM prompt generation.
6. Prompt before switching a reference-using workflow to a new host: display exact destination origin and whether personal photos will be uploaded.

## Request/response compatibility

### OpenAI-like image generation
- Default route: `POST {baseUrl}/images/generations` (normalize `/v1` once).
- JSON request with `model`, `prompt`, optional supported `size`, `quality`, `n`; validate options against probed/documented capabilities.
- Accept `data[].b64_json` and, where provider supports it, `data[].url` with **strict safe media fetching** (HTTPS or explicitly approved loopback, allowlist/redirect rules, byte/time limits). Do not leak the API key to a redirected host.
- Validate decoded MIME, raster dimensions and output size before saving.
- Incompatible/nonstandard services can use a future named adapter; “OpenAI-compatible” is a declared contract, not blind pass-through of unknown fields.

### Reference/edit image
- Default route: `POST {baseUrl}/images/edits`, typically multipart form. Providers vary on `image` versus `image[]`, mask, allowed file counts, sizes and output format.
- Use a per-adapter capability/shape declaration and real probes. Clearly disable the reference-photo control for backends without a usable edit or image+text multimodal path. **Do not silently drop reference images.**
- A chat/completions endpoint accepting images as input is not necessarily an image-output endpoint; only advertise it after a successful end-to-end test.

### Text model / scenario generation
- Optional `POST {baseUrl}/chat/completions` (or explicit Responses-compatible mode later). Use a **separate** model and test. Never send imported reference photo bytes to the LLM prompt service unless explicitly necessary and consented.
- Scenario variable expansion can work fully offline without an LLM, so a single image provider is sufficient for the MVP.

## Code architecture

Current `ImageGenerationServiceFactory.CreateService(ImageModelOption)` switches on hard-coded `ImageProvider` and `ImageModelCatalog.Options` is static. Refactor to:

```text
UI -> ProfileStore (no plaintext secrets) -> ProviderRegistry
                                         -> IImageProvider
                                            - BuiltinOpenAiAdapter
                                            - BuiltinGoogleAdapter
                                            - OpenAiCompatibleAdapter
                                            - CodexExperimentalAdapter (separate bridge)
UI -> ScenarioComposer -> ImageGenerationRequest (prompt, refs, options)
   -> GenerationCoordinator -> IImageProvider -> image validation
   -> FileService/DatabaseService -> Wallpaper
```

Suggested code boundaries:
- `Services/Providers/ProviderProfile.cs`, `ProviderRegistry.cs`, `ProviderCapabilities.cs`
- `Services/Providers/OpenAiCompatibleProvider.cs` (HTTP client injected)
- `Services/Providers/ProviderSecretStore.cs` (Windows secure store, test with in-memory fake)
- `Services/ImageGen/ImageGenerationRequest.cs` / result type with image bytes
- `Services/Generation/GenerationCoordinator.cs` (calls provider, persistence and wallpaper)
- `Views/SettingsView` or extracted `ProviderSettingsView`

Use `HttpClientFactory` or comparable long-lived HTTP clients, cancellation tokens, structured errors and a safe bounded retry policy. Ensure provider-specific quality/size parameters do not leak across adapters.

## Credential migration

Today `Services/Settings.cs` serializes API keys and some DeviantArt secrets to `%APPDATA%\WallTrek\settings.json` in plaintext. Design a schema-versioned migration:
1. On first upgraded launch, read legacy JSON locally.
2. Place secrets in Windows Credential Manager or DPAPI-protected user-scope store.
3. Persist only credential references and nonsecret provider settings.
4. **Only after successful vault persistence**, remove plaintext values and atomically rewrite JSON. Keep a recoverable migration path for interrupted upgrades without committing or logging raw data.
5. Test startup with legacy settings, missing vault, corrupt JSON, different Windows user profile and failed migrations. Never copy secrets to diagnostic bundles.

No sharing of third-party keys with Codex. A provider receives only its own explicitly configured auth, and only its chosen photo requests. Disable redirects or strip authorization across origins.

## Usability & reliability

- Support multiple named profiles and easy switching; make test-result status and error reason visible, not a single red “failed”.
- Prefer per-operation fallback choices requiring explicit user consent for photo destinations.
- Timeout should reflect image generation (e.g., configurable 2–5 minutes, adjusted by backend), not a hard 12-second wall. Retry only transient transport/429/5xx when safe; beware charging for duplicate image jobs.
- Show known vs unknown costs; if an unofficial Codex mode uses account quota, show `included account limits may apply` rather than `free`.
- Keep all secrets/photos local between runs; do not add analytics or a hidden cloud service.

## Tests to require

1. Fake OpenAI-compatible image endpoint: base path normalization, `b64_json`, URL output, 401, 404, 429/Retry-After, 500, invalid bytes, timeout/cancel.
2. Fake image edit endpoint: 1–5 refs, multipart field variants, unsupported operation error.
3. Custom image + separate chat provider working independently.
4. Two profiles with distinct keys never mix keys or send credentials on redirect/host switch.
5. Existing OpenAI/Google choices still generate with legacy saved preferences.
6. Repeated retries/cancellation leave existing wallpaper and archives intact.
7. Credential migration round trip and no secrets in exported settings/logs.

Release gate: at least one **real** non-OpenAI origin verified in Windows; all other provider compatibility statements must be specific to successful tests rather than assumed.

# Codex Connected Account — Authentication and Image Generation Adapter

Status: **design / feasibility gate**. This is not yet implemented. Read [ROADMAP.md](ROADMAP.md) first.

## User story

A person who **already installed and logged into Codex locally** may choose "Codex (experimental)" as the wallpaper image generator without buying an additional public API key. The app should detect the installation/session and attempt an opted-in image-generation operation using the same user's local Codex environment. A different Windows user account must not inherit access.

**Do not equate a ChatGPT subscription or a Codex login with guaranteed access to a stable image API.** Codex's image-generation tool exists, but scripted `codex exec` does not currently give a documented stable image-bytes API and platform/account capabilities vary.

## Priority order: supported integration first

1. Find installed `codex` executable through `PATH` / explicit user-selected executable. On Windows, probe command resolution, not guessed installer paths. Respect `CODEX_HOME` if set, otherwise Codex's per-user standard home (normally `%USERPROFILE%\.codex`). **Never scan other users' profiles.**
2. Check `codex --version` and `codex login status` with safe bounded subprocesses. These only indicate install/login, **not** image-generation eligibility. Present precise status with troubleshooting buttons; do not log command environment or credentials.
3. Investigate a documented CLI/SDK/app-server flow that yields an image file or stable image artifact event **in non-interactive Windows runs**. The integration must be able to supply a reference photo, collect a real image output and validate it, with a cancel/timeout path and no human chat interaction.
4. Only if (3) succeeds implement the official adapter as the default. **Do not simulate successful generation** by writing a placeholder or by returning assistant text.
5. If (3) is not available, leave official mode unavailable and optionally offer the isolated third-party helper below.

## Experimental helper path (separate, opt-in)

Candidate: [jdmnk/codex-imagegen-cli](https://github.com/jdmnk/codex-imagegen-cli), Apache 2.0, alpha. It reports `generate` and `edit` with existing Codex file-based ChatGPT authorization, PNG/WebP output, and local reference image input. It directly calls undocumented `chatgpt.com/backend-api/codex/...` endpoints and reads `auth.json` itself. **This is not an official public API or a reliability guarantee.**

Implementation boundaries:

- Ask the user to enable **Experimental Codex image backend** and acknowledge compatibility/account-terms risk. Offer other provider as recommended fallback; no automatic credential bypass.
- Use a separately installed helper launched as a subprocess; do **not** vendor its source into the WinUI app without a code/security/license review.
- The WallTrek process should **not open or parse `auth.json`**, refresh tokens, copy tokens, or pass account secrets as command arguments. If a helper reads local credentials, make that visible in the UI and documentation. Restrict executable path to a user-chosen or validated location; no auto-install or unverified download.
- If a future official command surfaces saved images, prefer it and retire the experimental bridge.
- Call the helper with an argument list (`ProcessStartInfo.ArgumentList`), not a shell interpolation; set controlled working directory, output path, env, and timeout. Use app-owned random temporary paths. Avoid passing sensitive prompts in command line where feasible (prompt file with restricted permissions).
- Detect helper availability/version and required runtime (`uv`/Python for this candidate). Preview and test **generation and edit separately**; do not claim reference support until a real test passes.
- For an edit scenario, pass up to the helper's tested reference limit; keep identity/content versus style file roles distinct. Ensure prompt includes explicit identity-preservation instructions.
- Verify file exists, is within sandboxed output directory, has valid PNG/WebP magic/dimensions and is under byte/pixel limits. Decode and persist atomically; clean temporary files after.
- Parse structured exit/status when available. Never log stdout/stderr verbatim because error streams could include sensitive content. Redact paths, user names, image data and credential content.
- Return typed errors: `NotInstalled`, `NotSignedIn`, `UnsupportedAccount`, `ToolUnavailable`, `RateLimited`, `Timeout`, `InvalidImage`, `BackendChanged`, `Cancelled`. Show retry guidance only when safe.
- No automatic fallback to an unrelated API provider when references are involved; switching vendors requires prior user approval for that vendor receiving the images.

## Suggested contract

```csharp
public interface IImageProvider
{
    string ProviderId { get; }
    Task<ProviderCapabilities> ProbeAsync(CancellationToken ct);
    Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken ct);
}
```

The future `CodexCliImageProvider` invokes a dedicated `ICodexBridge` (official or experimental) and has no direct dependency on WinUI. The bridge returns validated image bytes to the shared result contract. Persist provider/adapter version and public capability metadata, **never credential/token contents**. Gate experimental availability in the settings screen and feature flag, with actionable test flow.

## Security and account policy checks

- Codex credentials belong to the local user and Codex process; do not redistribute, synchronize, collect, upload, or expose them to our server.
- A session file being present does not authorize its ingestion by WallTrek. Prefer invoking installed signed-in tooling. Any third-party credential-reading helper requires informed opt-in and a code/terms review.
- Don't use cookie extraction, browser-session injection, hidden token scraping or bypasses to reconstitute Codex access.
- Check current service/account terms before shipping a publicly distributed experimental adapter. If prohibited or technically unstable, disable and retain official API and other providers.
- Treat per-account limits as finite; never rotate accounts, evade quotas, or brute-force retries. Honor `Retry-After` where surfaced; bounded backoff.
- Explicitly display when reference photos are sent to an external service, even when using existing ChatGPT/Codex authentication.

## Feasibility spike checklist (Windows only)

1. Is `codex` installed? Is `codex login status` successful? Does its config use file, keyring, or another credential store?
2. In a disposable scratch directory, run one **text-only** noninteractive generation and confirm an actual valid image file.
3. Run one **edit** operation with a consented sample photo, confirming the photo was used. No sensitive real user images in fixture repository.
4. Exercise cancel, timeout, missing helper, expired login, refusal, account limit, invalid output and no image tool. Collect redacted outcome metadata.
5. Record CLI/helper versions and exact supported Windows behavior in a future dated `docs/experiments/codex-*.md`.
6. **Decision:** supported and scriptable = implement official; unofficial helper passes security/policy review = opt-in experimental; neither passes = clearly mark unavailable and offer custom API.

## Why this is gated

- [Codex built-in image-generation tool source](https://github.com/openai/codex/blob/main/codex-rs/ext/image-generation/src/tool.rs) describes generation/editing and reference paths.
- [Unofficial helper README](https://github.com/jdmnk/codex-imagegen-cli#readme) explicitly states that it calls undocumented backend endpoints and there is no stable public API contract.
- [Reported issue: codex exec image tool unavailable in some Windows/WSL sessions](https://github.com/openai/codex/issues/19133) means neither enabling a setting nor successful login proves image generation will work.

The immediate implementation goal is **a truthful connection/capability test**, not silent reading of `~/.codex/auth.json`.

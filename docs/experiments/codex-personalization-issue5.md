# Issue #5 — Codex experimental reference editing for personalized wallpapers

Implementation branch: `feat/issue-5-codex-personalized-edit` from merged `main` / PR #4.

## What this connects

The existing photo library, prompt scenario composer, provenance SQLite history and automatic wallpaper application are reused unchanged. Selecting **Codex (experimental)** as the image model while a scenario has 1–5 enabled photos now routes the prepared prompt and sanitized photo derivatives to the existing `CodexImageBridge`, using its `CodexImageRequest` and `CodexReferenceRole` types through an additive `IImageProvider` adapter.

- Identity and content photos are handed to the helper as `--image`. One optional style photo is handed as `--style-image`. No style-only batch and no second style photo, because the current helper supports only one style image and needs a content image. The user sees a clear error rather than a silently lost reference.
- The scenario composer supplies identity, facial-feature, visual style, exclusions, and a non-repeating variable combination. The existing Codex bridge adds an identity-preservation reminder. **Face fidelity is best effort, not guaranteed.**
- All photo uploads use the library's EXIF-stripped, 2048px-limited JPEG derivatives, not original source paths. The new adapter writes random short-lived JPG files in a user-scoped `%LOCALAPPDATA%/WallTrek/CodexReferences` subfolder. On Windows, the per-call directory ACL is restricted to the current user's SID and inherited by its files. Paths passed to the helper contain random IDs, not original filenames. Temporary reference files are removed in `finally` after success, timeout, cancellation, or typed process errors; a force-killed process can still leave files, so inspect the private directory when troubleshooting. No credentials are read/copied by WallTrek.
- **Consent** is required in Personalize: choose `Codex experimental → https://chatgpt.com`, read the disclaimer and check the explicit upload authorization. Existing library approval is scoped to scenario, provider GUID, ChatGPT origin and selected photo IDs/roles; changing photos or scenarios clears approval. The helper actually sends requests to undocumented ChatGPT backend paths; their precise endpoint is not an OpenAI-supported public API and could change.
- Clicking **Check Codex login/helper** performs only installation/session tests; this is NOT proof that the ChatGPT account supports image generation or editing. The separate **Test Codex edit with approved photos** consumes account quota and requires a real returned decoded PNG, showing a preview and size but never applying that test output to the desktop. The pre-existing Settings → Codex probe offers another independent generate/edit test.
- Running Generate Image (or the existing interval-based auto-generation action) with Codex selected uses the actual Codex edit and, only after successfully saving the wallpaper plus metadata, uses the existing `Wallpaper.Set` path. A failed Codex call has **no provider fallback**, does not replace the wallpaper and does not advance the scenario variation. Generated images and exact resolved prompt/provider stay in the existing history tables.

## Availability and failure states

`CodexImageBridge.ProbeAsync` detects Codex installation, local login, the separate helper and its version. Successful probes report image capability **unverified** until a real image test. A real edit can return typed `NotInstalled`, `NotSignedIn`, `UnsupportedAccount`, `ToolUnavailable`, `RateLimited`, `Timeout`, `InvalidImage`, `BackendChanged`, or `Cancelled`. No automatic account rotation, forced retries, browser cookie scraping or arbitrary token reuse.

The unofficial `codex-imagegen-cli` helper may itself read local Codex credentials and call undocumented endpoints. This integration does not endorse its stability, terms compatibility or safety; users must enable this mode and review the helper separately. It is **not** a public Codex Images API and uses no secret copied out of `auth.json`.

## Windows tests

CI (`codex-personalization-tests.yml`) runs fake Codex client tests for 1–5 image references, identity/content/style mappings, sanitized ephemeral paths and deletion, explicit host consent, opt-out, rate limiting, cancellation, no fallback and stable scenario state. It also reruns existing fake Codex bridge and personalization tests, and builds the complete WinUI app. No personal photo or real Codex sign-in is available to GitHub-hosted runners.

For a **real, consented Windows desktop test** using an already signed-in Codex CLI and separately installed helper, open PowerShell under the same Windows user as Codex, choose a nonprivate/disposable PNG/JPEG portrait and run:

```powershell
$env:WALLTREK_CODEX_LIVE_CONSENT = "I_APPROVE_CHATGPT_UPLOAD"
$env:WALLTREK_CODEX_LIVE_REFERENCE_PATH = "C:\test-fixtures\disposable-portrait.png"
dotnet run --project tests/WallTrek.CodexPersonalization.Tests/WallTrek.CodexPersonalization.Tests.csproj -c Release -- --live
Remove-Item Env:\WALLTREK_CODEX_LIVE_CONSENT
Remove-Item Env:\WALLTREK_CODEX_LIVE_REFERENCE_PATH
```

This optional check does a **real image edit** and asserts that a valid PNG was returned; it does not test face similarity automatically. Never use production personal images for a publicly shared test, never put log dumps or account files into CI, and do not describe the integration as production-ready until that real test and Windows GUI workflow succeed. The helper is experimental and may fail even with valid local sign-in.

### Manual regression checklist

1. With Codex option off, run ordinary OpenAI/Google and custom image generation; settings and history must remain available.
2. Turn Codex on, import 1–5 disposable portraits, activate a scenario with seven unique locations, preview and approve the exact ChatGPT destination. Verify a real test edit shows the edited result and visually retains identity.
3. Switch photo role/active selection, confirm the old approval is rejected. Select a different custom host, verify no Codex approval is reused.
4. Verify 2 style-only images or multiple style references fail **without** dispatch.
5. Make a Codex edit fail (not signed in/rate limit/invalid output) and verify the old wallpaper remains intact and the next scenario preview is unchanged.
6. After a real successful image, verify desktop wallpaper, stored file, `GeneratedImages` and `PersonalizedRuns` provenance; no credentials or private images in diagnostics.
7. Test Windows shutdown/force-kill leftover temp recovery separately; reliable wall-clock wake/reboot scheduling is ROADMAP step 6, **not part of issue #5**.

Rollback: deselect the experimental Codex model or disable it in Settings. Existing photos, scenarios and history are left in their local user directories; there are no new database schemas in this PR.

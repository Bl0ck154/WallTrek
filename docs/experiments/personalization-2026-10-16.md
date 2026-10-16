# Personalization: reference photos and scenarios — 2026-10-16

Status: **stacked Draft PR #3**, based on PR #2's `feat/universal-provider-profiles`. This is **not shipped in main**. PR #1 Codex helper remains a separate feasibility-gated adapter. No edits in either agents' `Services/Providers/*` or Codex files.

## User flow

From Main → **Personalize: photos & scenarios**:
1. Import PNG/JPEG (select 1–5 files per import). Library can retain more; only up to **five enabled** photos can be sent in one generation. Select any photo to preview, label, enable/disable, assign Identity / Content / Style, mark a primary identity portrait, or delete the library copy. App-stored originals and derivatives live in `%LOCALAPPDATA%/WallTrek/Personalization/originals` and `uploads`. The original source location is never modified, and source paths/names are not included in the provider request.
2. Create or select a scenario. Write text containing e.g. `{{place}}` / `{{weather}}`; put variant pools on separate lines: `place = Prague | Lisbon | Vienna`. Add Style and Avoid instructions, Save and activate, then **Preview next final prompt**. There is no LLM step capable of erasing the identity clause. Preview is deterministic until a successful run, even after reopening the editor.
3. For images with references, use Settings → Providers to **test a real edit** first. In Personalize, select the exact custom provider/destination origin and explicitly tick the upload consent checkbox; the approval is bound to the scenario, provider GUID, destination origin, selected reference IDs and roles. A changed scenario or reference invalidates approval. No automatic vendor switch, even if the original provider fails. If no approval/edit support, fail **before sending photos**. Image pricing/availability depends on provider.
4. On Main select the same custom provider in Image Model and click Generate Image. If a scenario is active, the full resolved prompt (rather than old free-text input) is submitted through `IImageProvider`; when references exist, the adapter invokes its separate `EditAsync` using **sanitized JPEG derivatives**. Built-in OpenAI/Google keep working for prompt-only scenarios; they currently do **not** accept references through their legacy interfaces and refuse them with an actionable error.
5. A successful result is saved to original `GeneratedImages` SQLite history plus additive `PersonalizedRuns` table. It records the exact rendered prompt, variation values, IDs (not photo bytes), scenario name, provider/model, date, output filename. History can be viewed inside Personalize. Only after persistence does the scenario advance to the next unused combination. Failed output or cancelled work does not advance it or replace the wallpaper.

## Variation/quality/security notes

- Identity instruction covers face shape/features, skin tone, natural proportions, and maintaining the same recognizable individual. This is a *prompt instruction*, not a guarantee of visual identity. Visually inspect real samples.
- Scenario uses all cross-product combinations (capped at 4,096 for predictable memory/latency), deterministic pseudo-random ordering by scenario ID/cycle and committed result count. No repeated *successful* combination until the available pool is exhausted. On rollover, avoids immediate repeat if at least two combinations exist. Failures/retries reuse the same prepared prompt; changing pools resets the cycle.
- Source PNG/JPEG is validated via Windows decoder, dimensions <=12,000 with <=64 MP and file <=20 MiB. Outgoing JPEG is re-encoded/resized to maximum 2,048px; EXIF/geolocation discarded. The source image remains in app-local storage until deleted. More formats, e.g. WebP reference imports, are intentionally unsupported until Windows decoder audit.
- Only provider ID/origin and selected reference-set consent are persisted. No API keys, Codex credentials, photo data, or outgoing binary appear in repository/logs. Deleting photo purges app-owned original/derivative but keeps historical metadata; if original privacy removal from metadata is required, separately delete generated history.
- There is no reliable wall-clock schedule migration in this PR (roadmap milestone 6). Current interval/auto generation uses active scenario whenever generated, but OS wake/reboot and DST have not been hardened.
- Preview is of the **final text prompt**, not an actual output image. The exact same provider can still behave differently each run.
- Current UI is a modal editor, not a standalone route. Human usability and accessibility smoke tests are required on Windows.

## Tests and release gates

- Isolated fake tests: seven unique scenarios, stable previews, cycle rollover, invalid variables, import and reference roles, strict active five-photo cap, consent isolation/change invalidation, fake `IImageProvider` edit behavior and unsupported-edit refusal, SQLite provenance roundtrip.
- Windows build should be green before merging; record the corresponding GitHub Actions URL.
- Manual test with disposable consented photos: import/restart/role edit/remove, two provider hosts and change of destination, one genuine edit with a **verified** profile, inspect visually whether face remains recognizable. No claims without real result.
- Verify the original prompt-only OpenAI/Google flow, upscaling, wallpaper set, archive and tray still work.
- Verify no personal image is sent to an unverified endpoint (EditVerified false), and no generated file replaces wallpaper after cancelled/failed personalization.
- Merge order: PR #2 first, retarget this PR to main; Codex PR #1 needs its own conflict resolution and live Windows test. Do not overwrite shared provider files.

## Rollback / recovery

Deactivate the active scenario from Personalize to restore legacy generation. An app downgrade that lacks this PR ignores `library.json` and the additive SQLite `PersonalizedRuns` table; existing `GeneratedImages` remain readable. On file corruption the app must surface the issue rather than replacing the private library. Preserve `%LOCALAPPDATA%/WallTrek/Personalization` if you intend to restore the library later.

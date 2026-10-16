# Instructions for coding agents (this fork)

This fork extends WallTrek into a personalized daily-wallpaper generator. Existing upstream behavior is production functionality, not disposable scaffolding.

## Read first

1. `CLAUDE.md` — upstream architecture, build/test commands and conventions.
2. `docs/ROADMAP.md` — product scope, milestones, acceptance criteria and file ownership.
3. `docs/PROVIDER_ARCHITECTURE.md` — request/capability model and arbitrary API hosts.
4. `docs/CODEX_INTEGRATION.md` — staged feasibility and security rules for a connected Codex account.

## Operating rules

- Work in this fork; do not push changes to upstream `swax/WallTrek`. Preserve upstream MIT license and attribution.
- Treat docs as *planned*, not implemented. For implementation, open small, reviewable PRs by milestone. Never claim working Codex image generation without a real Windows end-to-end image file test.
- Do not open/copy/exfiltrate Codex `auth.json`, user API keys or private reference photos. Never commit real user image assets or secrets.
- Prefer invoking already-installed signed-in Codex tooling, rather than extracting tokens. Treat `codex-imagegen-cli` as unofficial, experimental and opt-in pending security/policy checks.
- Do not silently upload reference photos to a new provider or switch remote host. Explicit user consent is required.
- Keep existing Google/OpenAI paths, tray, history, optional upscale and DeviantArt features functioning. Make scenario/reference/provider logic independently testable rather than piling code into `MainView.xaml.cs`.
- The product is Windows WinUI 3. Validate builds/functional behavior on Windows. **Do not build it on shared production hosts.** Docs-only commits require no remote build.
- Include tests, migration/rollback notes and a manual validation checklist in each feature PR. Update roadmap milestones from Planned to Implemented only after evidence is attached.
- Separate parallel work: provider contracts, Codex feasibility spike, and UX/schema design may proceed in parallel; merge shared types before adapters or UI wiring.

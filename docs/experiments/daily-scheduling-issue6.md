# Issue #6 — reliable daily wallpaper schedule (2026-10-16)

**Implementation: Draft PR #9. Not a shipped release.** Old interval mode remains default for existing users.

## User-facing mode

Settings → General → Auto-generate wallpapers:
- **Every N hours:** original interval mode with persisted next timestamp.
- **Daily at chosen clock time:** 24-hour time picker and zone list. Default zone is **Follow Windows local timezone** (automatically adapts when user changes the Windows timezone); users can instead pin a named timezone, e.g. Eastern Standard Time. The app writes an activation timestamp when the user enables/changes this schedule. A newly enabled past daily time is not run immediately; the first run is the next occurrence.
- Daily requires **Run on Windows Startup**. The current application registers an HKCU Run entry that starts the app at **the next Windows user sign-in**, including after reboot. It does **not** start before user login, wake a sleeping PC, run as a Windows service, or recover missed tasks if the user never signs back in. There is no external server component. The tray app stays running in the signed-in user session.
- A background ThreadPool loop checks elapsed real wall-clock time every 30 seconds, regardless of WinUI window visibility. After the app starts/wakes, it processes **only the latest missed due slot**, not every missed date, so prolonged downtime never floods the account with generations.

## DST and time zones

All due times are calculated with TimeZoneInfo, not arithmetic on local DateTime. On spring-forward, a nonexistent time is mapped to the **first valid minute** after the gap. On fall-back, an ambiguous clock time is mapped to the **earlier real occurrence** (larger UTC offset), then protected by a one-per-local-date slot key, even if the system clock moves backward. Time zone rules and Windows local timezone are refreshed while polling. A user-selected fixed timezone continues to follow that region's DST rules, regardless of the PC's location.

### Exactly once / crash policy

`%APPDATA%\WallTrek\daily-schedule.json` is an atomic, versioned journal. Before generating, the scheduler takes a cross-process exclusive lock file and writes a durable `PendingSlot` claim, keyed by configuration signature, region and local calendar date. A second WallTrek process cannot start the same slot while the first holds the lock. The UI also prevents concurrent manual/automatic generation.

- After successful save/history/apply, the journal marks that slot **Applied**.
- On an acknowledged generation failure, the journal marks the slot handled but failed; **the old wallpaper is retained** and no automatic duplicate paid attempt occurs. The next local day remains eligible.
- If the UI was already busy **before an image request was sent**, dispatch may be deferred after 1 minute and then after 5 minutes; at most **3** attempts. This is a bounded dispatch retry, **not** an unbounded image retry.
- After a hard crash/power loss while an image operation was in flight, the outcome is inherently unknown (it could have been billed, and/or already set the wallpaper). On restart, the journal conservatively marks the attempted slot **Interrupted / unknown** and **does not automatically repeat** it. This guarantees no intentional double dispatch but may sacrifice one wallpaper in a crash. No operating system can offer full exactly-once network side effects without server-side idempotency.
- A damaged/unsupported journal is not overwritten. The schedule reports an error; inspect/recover the JSON manually or with a future repair UI.
- A user reconfiguring the mode/time/zone generates a new schedule signature and activation time. Disabling auto generation cancels polling. The previous wallpaper and generated image history remain untouched.

## Existing generation pipeline

The app now awaits the wallpaper generation result through the WinUI dispatcher rather than treating fire-and-forget calls as successful. If the generation fails, no wallpaper apply call occurs. The Win32 `SystemParametersInfo` result is checked and surfaced; the scenario variation advances only **after** wallpaper application. Generated output continues through existing provider/reference/upscale/history logic. No tokens/photos need to be copied or handled by the scheduler.

## Tests and rollout gates

Automated Windows tests cover DST transitions, two zones, invalid configuration, activation/next-day behavior, multi-day missed run on reboot, wake/catch-up with repeated polls, force-crash pending tombstone, failure/no duplicate retry, bounded busy deferrals, cross-instance lock and damaged journal protection. Windows CI also builds the app and runs the previous personalized scenario tests.

Manual Windows test required before release:
1. Enable daily 09:00 and the startup checkbox; Save. Restart app, verify mode/time/zone persisted. Check HKCU Run entry points to the actual WallTrek executable.
2. Set a near-future daily time using a disposable inexpensive image model; confirm exactly one wallpaper switch and history record. Deliberately fail the next run (e.g. nonexistent provider) and confirm previous wallpaper remains.
3. Suspend Windows across the planned due time and resume; verify run within approximately 30–60 seconds after normal process wake. Verify no duplicate next time the window appears, clock changes, or system resumes repeatedly.
4. Reboot and sign in after a missed daily time; verify one latest missed slot (not every missed day). Reboot while generating: pending unknown slot must not trigger a second billed call.
5. Test both DST paths (automated fake clock tests cover correctness; manual timezone UI selection and next-run label still required). Disable scheduler; ensure interval mode still generates on its configured interval and scheduled cancellation does not corrupt history.
6. Keep tray app running while window hidden. Verify two simultaneously started processes cannot generate the same daily slot. Try a busy manual generation as the scheduler fires and verify the bounded dispatch deferral.

**Rollback:** switch Settings mode back to Every N hours; the daily JSON journal is not consulted. Older versions ignore extra AutoGenerate JSON fields. Do not manually remove journal if at-most-once history matters.

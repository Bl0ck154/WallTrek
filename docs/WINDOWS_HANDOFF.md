# Testing WallTrek on a personal Windows PC

This is a portable, unsigned x64 **test build**, not a verified or signed release. It uses .NET 10 / Windows App SDK self-contained publishing, so running it should not require installing the .NET SDK or Visual Studio.

## Obtain the build
1. Open [Portable Windows test build](https://github.com/Bl0ck154/WallTrek/actions/workflows/windows-portable-test-build.yml).
2. Open the latest **successful** manual run for **main**, then download **WallTrek-Windows-x64-portable-test** from Artifacts. GitHub may require login.
3. Extract the complete archive into a new folder. Run **WallTrek.exe** (not the upstream fork release).
4. Windows may warn because this development build has no code-signing certificate. Confirm its origin before deciding whether to run it.

A portable install still writes user data to %APPDATA%/WallTrek, %LOCALAPPDATA%/WallTrek and %USERPROFILE%/Pictures/WallTrek. Back up these directories if you already use WallTrek.

## What to try
- Open Settings. In the Codex section, check **local Codex detected** and **signed in**. This is only a login probe, not a proof of image access. WallTrek must not ask you to paste tokens.
- For actual Codex images, an independently installed and inspected **codex-imagegen-cli** helper is necessary. This third-party tool reads locally stored Codex credentials and calls undocumented ChatGPT endpoints. If you have not reviewed and installed the helper, do not enable real Codex generation; test only the probe.
- Once the helper is present, explicitly enable experimental Codex, use **Test real generation** with a harmless prompt. Then import a disposable sample portrait in **Personalize**, set Identity, save a scene, preview the final prompt, explicitly approve upload to Codex/ChatGPT, run **Test real edit**, and try one manually generated wallpaper.
- Verify that failed generation does not replace the previous wallpaper, and check image history.
- Only after manual generation works, configure Daily at a future local time and enable Run on Windows Startup. Verify the next scheduled run and eventual restart/sleep behavior. The app will not wake or power on a sleeping/off PC and only resumes after Windows sign-in.

## Report back
Share whether the app launched, the Codex CLI and helper probe statuses, a sanitized error message/screenshot if any, whether actual generation/edit produced an image, and the build commit in BUILD_INFO.txt. Do **not** share Codex tokens, auth.json, personal photos, API keys, or unredacted sensitive logs.

See [release validation issue #7](https://github.com/Bl0ck154/WallTrek/issues/7).

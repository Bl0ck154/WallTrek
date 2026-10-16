# Windows portable test build (no installer)

This is an unsigned **test version** for Windows 10/11 x64, not a validated public release. It bundles the WinUI/Windows App SDK and .NET dependencies. The portable folder contains numerous internal DLL/runtime files; they are needed to run the app, but all reside inside one top-level WallTrek folder.

## First run

1. Open the [portable Windows build](https://github.com/Bl0ck154/WallTrek/actions/workflows/windows-portable-test-build.yml). Select the latest **successful** manual build on `main`.
2. Under **Artifacts**, download `WallTrek-Portable-Windows-x64` (GitHub sign-in may be needed).
3. Extract the archive to a writable user folder, for example `%USERPROFILE%\Apps\WallTrek`. The extracted archive already contains a `WallTrek` folder with `WallTrek.exe` inside. **Do not use Program Files and do not run any setup.**
4. Run `WallTrek.exe` directly. You can optionally make a desktop shortcut to it.

## Updates without reinstalling

1. Close WallTrek **completely**, including Quit from its system-tray menu. Running DLLs can remain locked while it is in the tray.
2. Download the newest successful portable artifact and unzip it.
3. Copy the **contents of the new WallTrek folder into your existing WallTrek folder**, replacing existing files. You do not need to run any installer or have admin rights.
4. Launch the same `WallTrek.exe`. Check `BUILD_INFO.txt` for the version/commit. If an old DLL appears incompatible or a library was removed, rename your old application folder as a backup, unpack a clean new folder at the **same path**, and start it.

Settings, scenarios, history, API credentials and generated images are stored under your Windows user profile, outside the application folder, so simply replacing binaries should not erase them. Back up those settings when upgrading from an old build. If you **move** the portable app to a different path, toggle **Run on Windows Startup** off and on in Settings so its shortcut points to the new location.

This is still a self-contained .NET/WinUI desktop app. The archive may be large, but no files have to be installed into Program Files, and no extra runtime or Visual Studio installation is needed to launch it.

## Smoke test

- Check that the window layout, Settings, image model picker and Personalize editor open.
- Test the local Codex login/helper detection. Codex image generation still requires a **separately installed, reviewed unofficial helper** and may not be supported by the signed-in account. Never send us `auth.json` or API keys.
- With a disposable photo and explicit consent, test a real reference edit if the helper is available.
- Test the wallpaper set action and daily timer after enabling Windows startup; the app cannot wake a sleeping/off computer.

Send a screenshot and a sanitized error message if anything breaks; release acceptance remains in [issue #7](https://github.com/Bl0ck154/WallTrek/issues/7).

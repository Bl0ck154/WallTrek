# Windows installer test handoff

This is an unsigned experimental Windows 10/11 x64 build, not a verified stable release.

## Installation

1. Open the [Windows installer workflow](https://github.com/Bl0ck154/WallTrek/actions/workflows/windows-portable-test-build.yml) and choose a successful run for the feature PR or main.
2. Download the artifact **WallTrek-Windows-x64-Installer-test**, then extract the ZIP wrapper. Inside is just one installer EXE.
3. Run **WallTrek-Windows-x64-Setup.exe**. It installs under your Windows user profile without administrator rights and can create a desktop shortcut.
4. Launch WallTrek from the Start menu. The installer bundles the .NET 10 and Windows App SDK dependencies, which live inside the installed application folder, not beside the downloaded installer.

This build is not code signed; verify the GitHub source and follow local Windows security policies. Existing app settings and user images remain after uninstall, intentionally.

## First real smoke test

- Check the compact main window and Advanced prompt tools at your normal display resolution.
- In Settings test the Codex CLI/login *probe*. Logged-in status is **not** proof that experimental image generation works. No tokens should ever be pasted into WallTrek.
- The separate unofficial codex-imagegen-cli helper is *not included*. It may access the currently signed-in Codex credentials and use undocumented endpoints; only install a reviewed helper if you explicitly accept its risks.
- With an eligible reviewed helper, test real image generation first. Then import disposable images, preview a scenario, explicitly consent to the destination and test reference editing. Existing wallpapers must remain unchanged on errors.
- Finally test the daily schedule after opting into Run on Windows Startup. The app does not wake or power on a computer; missed slots recover after sign-in.

Report back with a screenshot, error messages without credentials and whether Codex generated a real image. See [issue #7](https://github.com/Bl0ck154/WallTrek/issues/7).

The old portable ZIP workflow has been replaced with the installable executable.

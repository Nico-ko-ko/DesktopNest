# DesktopNest Implementation Plan

Date: 2026-09-26

## Scope

Implement the approved DesktopNest design exactly as specified:

- C# and WinForms.
- .NET Framework 4.8 using the C# compiler included with Windows.
- No third-party packages, Electron, WPF, installer, network, telemetry, or startup integration.
- Deliver source, equivalent project/build files, icon generation, `DesktopNest.exe`, and usage documentation.

## File Map

```text
DesktopNest.csproj
build.ps1
README.md
assets\DesktopNest.ico
tools\GenerateIcon.ps1
src\DesktopNest\
  AppSettingsStore.cs
  IconService.cs
  MainForm.cs
  Program.cs
  SelfTest.cs
  SettingsModel.cs
  ShortcutImporter.cs
  ShortcutLauncher.cs
  WindowPlacement.cs
dist\DesktopNest.exe
```

## Tasks

### 1. Data model and persistence

- Add `SettingsModel` types with version, title, bounds, maximized state, ordered item list, IDs, names, stored shortcut filenames, and optional target hints.
- Add `AppSettingsStore` using `System.Web.Script.Serialization` from .NET Framework.
- Create defaults when `data.json` is absent.
- Back up corrupt or incompatible JSON with a timestamp and return a user-visible warning.
- Save through a same-directory temp file and atomic replace.
- Verify with `--self-test`.

### 2. Shortcut import and launch

- Add `ShortcutImporter` to copy `.lnk` and `.url`, and create `.lnk` wrappers for executables, folders, and ordinary files through Windows Script Host.
- Generate unique IDs and preserve insertion order.
- Add `ShortcutLauncher` using ShellExecute semantics and target-existence checks.
- Ensure a failed import does not discard successful imports.

### 3. Application shell and icon grid

- Add WinForms entry point with `[STAThread]`, no console window, AppUserModelID, single-instance Mutex/event, and activation of the existing window.
- Add `IconService` using Windows Shell icon extraction.
- Add `MainForm` with standard resizable window behavior, title rename, drag/drop, scrollable auto-column icon grid, hover, tooltips, double-click launch, and context menus.
- Save title, bounds, maximized state, and ordered items.
- Restore bounds only when they overlap a visible screen.

### 4. Build assets and docs

- Generate the application icon from `tools\GenerateIcon.ps1`, producing `assets\DesktopNest.ico`.
- Add `build.ps1` using the Windows .NET Framework compiler directly.
- Add `DesktopNest.csproj` as an equivalent build description.
- Add `README.md` with run, persistence, and taskbar pin instructions.

### 5. Verification

- Run `build.ps1`.
- Run `dist\DesktopNest.exe --self-test` and require exit code 0.
- Launch the EXE and check process/window behavior.
- Exercise drag-in, launch, rename, remove, persistence, single-instance activation, and taskbar identity with a test shortcut.
- Confirm the original desktop shortcut remains untouched.

## Acceptance

The implementation is complete only when the EXE builds, self-test exits successfully, and the runtime checks in the approved design pass. Any unresolved issue must be reported only if it affects actual use.

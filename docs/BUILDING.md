# Building Kippix from source

This page is for people who want to change Kippix or build it themselves. If you just want to use Kippix, get it from the
[Releases page](../../../releases) instead (see the [install guide](GUIDE.md#1-install)).

## What's in the repository

| Folder | What it is |
|---|---|
| `Kippix/` | The fullscreen theme (XAML views, styles, images, sounds, the English texts in `Localization/en_US.xaml`). |
| `KippixHelper/` | Kippix Helper, the add-on that powers the theme: `src/` (C#), `build.ps1`, `extension.yaml`, `icon.png`. The in-game overlay is part of it (`src/Overlay.*.cs`). |
| `community/` | The Community gallery the theme reads (layouts, look packs, widgets). See `community/README.md`. |
| `docs/` | These guides. |

The theme and the helper always go together: the theme's screens are driven by the helper, so a theme without its helper
(or with a helper from another release) won't work properly. Give both the same version number.

## Requirements

- Windows 10 or 11
- [Playnite](https://playnite.link) 10 or newer, installed or portable
- Nothing else: the helper is compiled with the C# compiler that comes with Windows (.NET Framework 4, `csc.exe`). No
  Visual Studio needed.

## Build and install the helper

1. Close Playnite (an add-on can't be replaced while Playnite is running).
2. In PowerShell, from the `KippixHelper` folder:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
   ```

   `build.ps1` finds Playnite (in `%LOCALAPPDATA%\Playnite`, `Program Files\Playnite` or `Program Files (x86)\Playnite`),
   compiles every file in `src\` into `build\KippixHelper.dll`, makes a shareable `KippixHelper.pext`, and installs the
   add-on into Playnite's `Extensions\KippixHelper` folder.

   - Playnite somewhere else? Add `-PlaynitePath "D:\Apps\Playnite"`.
   - Only want to check it compiles? Add `-CompileOnly`.
   - Errors are shown in the window and saved in `KippixHelper\build.log`.

## Install the theme while developing

Playnite loads fullscreen themes from `%APPDATA%\Playnite\Themes\Fullscreen\<theme id>` (for a portable Playnite:
`<Playnite folder>\Themes\Fullscreen\<theme id>`). The theme's id is `Kippix_Kipping0`.

The easiest way to work on it is a link, so your changes show up the next time fullscreen starts:

```bat
mklink /J "%APPDATA%\Playnite\Themes\Fullscreen\Kippix_Kipping0" "C:\path\to\repo\Kippix"
```

Then pick **Kippix** as the fullscreen theme: in fullscreen mode open Playnite's menu, then Settings › Visuals › Theme, and
restart when asked.

## Making a release

1. Set the same version in `Kippix/theme.yaml` and `KippixHelper/extension.yaml` (`Version: 1.0.0`).
2. Build the helper (above). `KippixHelper\KippixHelper.pext` is the add-on package.
3. Pack the theme with Playnite's Toolbox (it comes with Playnite):

   ```bat
   "%LOCALAPPDATA%\Playnite\Toolbox.exe" pack "C:\path\to\repo\Kippix" "C:\path\to\output"
   ```

   That makes the `.pthm` theme package.
4. Make a GitHub release whose tag is the version (`v1.0.0`) and attach both files (`.pthm` and `.pext`).
   Kippix's **Check for updates** (Settings › Help) compares this tag with the installed version, so keep the tag and the
   two `Version:` lines the same.

## The Community gallery

The theme downloads the gallery from `community/` in this repository (the `main` branch, through
`raw.githubusercontent.com`). People share layouts, look packs and widgets through the issue forms in
`.github/ISSUE_TEMPLATE/`; you check each one and add it by hand. `community/README.md` explains the files and the steps.

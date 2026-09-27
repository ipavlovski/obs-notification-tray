# OBS Rec Indicator

A small Windows tray app that shows one icon per recording made by the OBS
[Source Record](https://obsproject.com/forum/resources/source-record.1285/) plugin.
Each icon is the OBS icon with a red dot (recording) and a custom overlay in the
bottom-right corner, so 3 active recordings = 3 icons.

A grey OBS icon is always in the tray. Click it to open the settings.

A recording counts as active while its file keeps growing. Each tracker watches the
newest file in a folder that matches a wildcard pattern. When that file stops growing
for the "Stopped after" time, its icon hides, or turns grey if you choose that.

## Install

Run `ObsRecIndicator-Setup-<version>.exe`. It installs for the current user only, so no
admin rights are needed. It adds a Start Menu entry and, unless you untick the option,
starts the app when you sign in. The first time the app runs, it opens the settings window.

Uninstall it from *Settings → Apps*. Your config in `%APPDATA%\ObsRecIndicator` is kept.

## Set up

1. In OBS, give each Source Record filter its own **Filename Formatting**, for example
   `Game %CCYY-%MM-%DD %hh-%mm-%ss`, `Browser %CCYY-%MM-%DD %hh-%mm-%ss`.
2. Click the grey tray icon to open **Settings**:
   - **General**: the folder Source Record writes to, how quickly to notice that a
     recording stopped, whether idle recordings hide or show grey, the base icon, the
     dot color, and *Start with Windows*.
   - **Recordings to watch**: add one entry per filter. Give it a name, a file pattern
     (for example `Game *.mkv`) and an overlay, which is either a text badge or an image
     (`.png`/`.ico`, or an `.exe` to borrow its icon). The preview shows how the icon will
     look, and *Files found* shows what the pattern currently matches.
3. Click **Save**. The app reloads with the new settings.

Windows 11 puts new tray icons in the overflow flyout. To keep them on the taskbar, turn them on
under *Settings → Personalization → Taskbar → Other system tray icons*.

## Using it

- Click the grey main icon to open Settings. Right-click it for Open config folder, Reload
  and Exit.
- Hover over a recording icon to see the tracker name, how long it has been recording and
  the file name. Double-click it to open the folder with the file selected.
- Running the exe again while it's already running asks whether to reload the config or quit.
  To script this, use `ObsRecIndicator.exe --reload` or `ObsRecIndicator.exe --quit`.

## Config file

Settings are stored in `%APPDATA%\ObsRecIndicator\config.json`. To run a portable copy,
put a `config.json` next to the exe, or pass a path as the first argument:
`ObsRecIndicator.exe D:\obs\indicator.json`. Every option is described in
[config.sample.json](config.sample.json). Saving from the settings window rewrites the
file without comments.

## Building

Both scripts run on Windows:

- `build.cmd` produces `bin\ObsRecIndicator.exe`. It uses the C# compiler that ships with
  Windows (.NET Framework 4.x), so no SDK is needed.
- `package.cmd` runs `build.cmd` and then builds `dist\ObsRecIndicator-Setup-<version>.exe`.
  It needs [Inno Setup 6](https://jrsoftware.org/isinfo.php): `winget install JRSoftware.InnoSetup`.

The version number comes from [src/AssemblyInfo.cs](src/AssemblyInfo.cs).
[assets/app.ico](assets/app.ico) was generated with `ObsRecIndicator.exe --make-ico assets\app.ico`.
`ObsRecIndicator.exe --render-icons [config] [outDir]` writes every tray icon as a PNG
for previewing.

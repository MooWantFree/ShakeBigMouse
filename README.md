# ShakeBigMouse

English · [简体中文](README.zh-CN.md)

When you lose track of the pointer on Windows, shake the mouse back and forth a few times to make it larger. The gesture works across monitor boundaries, too. Once you spot the pointer, stop shaking and it shrinks back to its usual size. Press a mouse button to shrink it more quickly.

## Getting started

On a 64-bit PC running Windows 10 or later, double-click `ShakeBigMouse.exe`. Look for the black arrow in the notification area of the taskbar. If you don't see it, open the hidden icons menu next to the clock.

Double-click the arrow, or right-click it and choose **Settings…**, to adjust the effect. Choose **Disable** to pause it and **Enable** to turn it back on. **Exit** closes the app and restores your system cursors.

Run `ShakeBigMouse.exe --show` to enlarge the pointer through the running instance without shaking. It reaches your configured maximum, holds for the **Command-line duration** under **Shake → Command line** (0.5–30 seconds; default 1 second), then shrinks normally. This duration is independent of the hold after shaking. Disable, fullscreen and application exclusions still apply. The command exits without starting a tray app if none is running.

## Adjusting the shake

Open the **Shake** tab in Settings. By default, a stronger shake makes the pointer larger, up to 10 times its usual size. Choose **Always reach maximum** if you want the same size each time.

| Setting | Default | When to change it |
| --- | --- | --- |
| Minimum shake distance | 1283 logical pixels | Lower this if a short shake is hard to trigger. It counts the total distance the pointer travels back and forth. |
| Screen diagonal threshold | 40% | This sets a second distance threshold based on the current monitor's diagonal. A shake must meet both thresholds. If lowering the minimum distance has little effect, try lowering this percentage too. |
| Detection window | 350 ms | Increase the window if you tend to shake the mouse more slowly. |
| Hold After Shaking | 0 seconds | Increase this if the pointer starts shrinking before you've had time to spot it. |
| Shrink Speed | 2.5× | Choose a speed from 0.5× to 3×. Lower values make the pointer shrink more slowly. |

You can drag each slider or type its value directly. **Preview** tries the current maximum size, command-line duration and shrink speed without saving. Clicking a mouse button, leaving Settings or closing the window cancels the preview.

**Apply** saves your changes and keeps Settings open. **Confirm** saves and closes the window. **Reset** puts the default parameters back in the controls while keeping your language and startup choices. The parameters take effect when you choose Apply or Confirm. Closing the window discards unsaved parameter changes.

## Fullscreen apps, language and startup

Settings opens on **General**, where you can manage language, startup, fullscreen behavior and excluded apps. The app pauses enlargement by default when a game or video is fullscreen. To use it there, turn off **Disable in Fullscreen**. To pause enlargement in particular apps, add them to **Excluded apps**. New exclusions record the executable's full path, so same-name programs in different folders can be excluded separately. The picker displays and searches paths; **Add current app** also records a full path.

Each exclusion shows the executable name and a shortened path; hover to see the full path. Check **Match process name only** to exclude every process with that name, regardless of its folder. Hover over the checkbox for a brief explanation. The path is hidden in this mode but retained; unchecking shows it again and restores matching against the original saved path. Use Apply or Confirm to save matching-mode changes. Older name-only rules remain checked and cannot switch to path matching until you remove and re-add the app. Some games draw their own pointers, which may stay the same size.

Under **General**, turn on **Launch at Login** if you want the app to start when you sign in. It's off by default; use Apply or Confirm to save it. Reset preserves its current checked state, including unsaved changes. The interface follows your Windows display language, using Simplified Chinese for Chinese and English for other languages. Choosing a language applies and saves it immediately, including the tray menu, without saving other pending edits.

## Windows builds

The **Build Windows ZIP** GitHub Actions workflow builds only Windows x64. It runs on pushes to `main`, tags starting with `v`, pull requests, and manual runs from the Actions tab. Open a successful run and download **ShakeBigMouse-win-x64.zip** from its artifacts. Artifacts are retained for 30 days.

Extract the ZIP and run `ShakeBigMouse-win-x64/ShakeBigMouse.exe`. The package includes the .NET runtime, both READMEs and the MIT license; no separate .NET installation is needed. Executables and build output are not stored in Git.

To build the same ZIP locally, install the .NET 10 SDK and PowerShell 7 on Windows, then run:

```powershell
pwsh -File ./scripts/Publish-Windows.ps1
pwsh -File ./scripts/Test-WindowsPackage.ps1 -ZipPath ./dist/windows/ShakeBigMouse-win-x64.zip
```

The output is `dist/windows/ShakeBigMouse-win-x64.zip`. `Publish-Windows.ps1` also accepts `-DotNetPath` for an SDK installed outside PATH and `-OutputDirectory` for another output folder.

The desktop behavior checks can be run locally with:

```powershell
dotnet run --project ./tests/BehaviorTests/BehaviorTests.csproj --configuration Release
```

These checks use real windows, monitors and cursors. The GitHub workflow compiles the test project and verifies that the packaged `--show` command starts and exits successfully without requiring a desktop test session.

## License

[MIT](LICENSE). Based on [nowonderwhyy/shake-to-find-cursor](https://github.com/nowonderwhyy/shake-to-find-cursor).

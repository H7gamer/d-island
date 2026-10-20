# D-Island developer handbook

D-Island is a Dynamic Island–style overlay for Windows, written in C#, WPF and .NET 8. User
documentation is in `README.md`. This file is for whoever works on the code, people and AI
assistants alike. It records what the code doesn't show: decisions and their evidence,
measurements, Windows pitfalls, how to test, and what is still open.

@CLAUDE.local.md

## Build, run and debug

```powershell
Get-Process DIsland -ErrorAction SilentlyContinue | Stop-Process -Force   # else the build can't overwrite the exe
dotnet build                       # Debug: includes the DISLAND_* debug hooks
dotnet run -c Release              # everyday use
.\tools\build-installer.ps1        # installer (needs Inno Setup 6), see "Installer"
```

- **Single instance:** only one instance runs at a time (`Local\DIsland.SingleInstance`). Launching
  the exe again opens Settings in the running instance, and `--settings` opens Settings at
  startup. `--probe-gpu-blur` is the throwaway child process that tests the GPU blur engine; exit
  code 0 means it works.
- **Files:**
  - Errors: `%AppData%\DIsland\error.log`. Settings: `%AppData%\DIsland\settings.json`. Shelf:
    `%LocalAppData%\DIsland\shelf.json`. To-do list and notes: `%LocalAppData%\DIsland\notes.json`. Motion artwork:
    `%LocalAppData%\DIsland\MotionArt\`. Identity setup: `%LocalAppData%\DIsland\Identity\`.
  - Don't overwrite a user's settings in tests without a backup.
- **Debug-only hooks.** These are compiled with `#if DEBUG`. Set one as an environment variable
  before starting the exe:
  - **Snapshots:**
    - `DISLAND_SNAPSHOT=<png>` renders the island's own visual tree to a PNG, after
      `DISLAND_SNAPSHOT_DELAY` ms (default 3000). It works even when screen capture doesn't.
    - `DISLAND_SETTINGS_SNAPSHOT=<png>` renders Settings (`DISLAND_SETTINGS_SCROLL=end` scrolls to
      the bottom first).
  - **Island state:**
    - `DISLAND_EXPAND=1|<Page>` opens the island (on a page such as `Calendar`) and keeps it open.
      `DISLAND_NAV=Tabs|Dots` sets the page navigation for the run.
    - `DISLAND_ACTIVITIES=timer,stopwatch` starts activities (shows the second-activity bubble).
    - `DISLAND_DEVICE=<name>` (or `-<name>`) fakes a device connecting (or disconnecting).
    - `DISLAND_ICS=<url>` uses that iCal link for the run, in memory only.
    - `DISLAND_ENERGYSAVER=1` forces the island's Energy Saver mode.
  - **Performance:**
    - `DISLAND_BENCH=1` expands and collapses every 700 ms. `DISLAND_STATS=<txt>` writes GC
      numbers.
    - `DISLAND_STARTUP_LOG=<txt>` logs CPU per startup phase. `DISLAND_SKIP=<names>` skips
      secondary services (privacy, focus, status, devices, notifications, weather, brightness).
  - **GPU blur:** `DISLAND_BLUR_LOG=<txt>` logs every thumbnail with its window class and HRESULT.
    `DISLAND_BLUR_ONLY=<class>` mirrors just that class plus the desktop. `DISLAND_THUMB_FLAGS=<n>`
    overrides the thumbnail flags.
  - **Other logs:**
    - `DISLAND_NOTIF_LOG=<txt>` logs the notification mode (live event or 2 s poll).
    - `DISLAND_PRIVACY_LOG=<txt>` logs mic/camera reads.
    - `DISLAND_MOTION_TEST=artist|album|<txt>` and `DISLAND_MOTION_PLAY=<mp4>` test motion
      artwork.
  - **Updater:**
    - `DISLAND_UPDATE_FEED=<url>` replaces the GitHub API URL. It allows `http://localhost:` asset
      URLs and checks after 3 s, ignoring the once-a-day limit.
    - `DISLAND_UPDATE_INSTALLED=1` treats the Debug build as installed.
    - `DISLAND_UPDATE_DRYRUN=<txt>` writes the install command instead of running it.
    - `DISLAND_UPDATE_LOG=<txt>` logs each state change and exception.
    - **Testing:** serve a small payload (a few MB) from a PowerShell `HttpListener`. A 50 MB write
      from a PowerShell job is very slow.
    - `DISLAND_WHATSNEW=<version>` shows the What's New window as if updated from that version.
  - **Animation:** `DISLAND_ANIM_FRAMES=<folder>` records the island → notch → island morph and
    hide → show as PNG frames every 40 ms (the shape change is in memory only).
  - **Snapshot timing:** the first launch after a build starts slowly. Allow at least 7 s or retry
    once before deciding that a snapshot failed.
- **Test scripts** in `tools/`:
  - `bench.ps1` measures idle and animating CPU plus memory (noise ±5 points; average 3 runs on
    the same power source).
  - `ztest.ps1` checks z-order.
  - `glasstest.ps1` puts a striped window behind the island for a blur check.
  - `build-installer.ps1` builds the installer.
- **Realistic benchmarks** come from an optimised build with the hooks compiled in:
  `dotnet build -c Release "-p:DefineConstants=DEBUG%3BTRACE" -o <scratch>\benchrel`, plus
  `DISLAND_BENCH=1`.
- **Shell pitfalls:**
  - Windows PowerShell is DPI-unaware: `CopyFromScreen` uses physical pixels while Forms
    positions are logical. Call `SetProcessDPIAware()` in test scripts.
  - `FindWindow` and `MainWindowHandle` can return 0 for the island's windows; use `EnumWindows`.
  - A `WinExe` gives no console output, so write results to files.
  - `SendInput` from a background script can't raise a window above the foreground app.

## Architecture and file map

| File | Role |
|---|---|
| `App.xaml(.cs)` | Startup: single instance, `--probe-gpu-blur`, Settings from a second launch, tray, error log. Settings are opened at `Background` priority and brought to the front with a Topmost toggle. |
| `MainWindow.xaml(.cs)` | The island: window, geometry, views, pages, alerts and all UI glue (large; organised in `// ----` regions). |
| `SettingsWindow.xaml(.cs)` | Live settings editor. Sections are generated in code (`Section`, `Slider`, `Choice<T>`, `Check`, `Note`), with a section sidebar. |
| `StateController.cs` | Transient alerts (priority-ordered) and persistent activities: timer > stopwatch > media > clock. The second running activity is `SecondaryView`. |
| `Spring.cs` | Damped spring with 240 Hz sub-steps. |
| `TrayIcon.cs` | WinForms `NotifyIcon` (the only WinForms/Drawing user; those implicit usings are removed in the csproj). |
| `GpuBlurBackdrop.cs` | GPU Liquid Glass engine: DWM thumbnail visuals with a DirectComposition blur and clip. |
| `WeatherIcons.cs`, `WheelScrolling.cs` | Vector weather icons; proportional wheel scrolling for every `ScrollViewer`. |
| `Interop/` | P/Invoke and Core Audio COM declarations, with vtable order matching the SDK headers. |
| `Services/AppSettings.cs` | All settings, enums, `ResetToDefaults` and the migrations in `Load()`. |
| `Services/MediaService.cs` | GSMTC: picks the playing session, splits Apple Music's "Artist — Album", timeline, seeking and accents. |
| `Services/MotionArtworkService.cs` | Apple Music motion artwork (opt-in, unofficial): lookup, token, HLS download, transcode and cache. |
| `Services/Artwork.cs`, `ColorTools.cs` | Image/GIF decoding at capped sizes; accent color extraction. |
| `Services/WallpaperEngineService.cs` | Wallpaper Engine detection, `-control` commands, theme, and exit/file watching. |
| `Services/WindowEventWatcher.cs` | Debounced (30 ms) WinEvent hooks. They drive the GPU blur sync, background detection, the WinUI fallback and keeping the island on top. |
| `Services/BackgroundDetector.cs`, `ScreenCapture.cs` | App or desktop behind the island (adaptive opacity); GDI capture for the fallback blur engine. |
| `Services/DeviceService.cs` | Device connect/disconnect from a `DeviceWatcher` over `DeviceContainer`s (`System.Devices.Connected`, `CategoryIds`), plus Bluetooth battery. |
| `Services/QuickAppsService.cs` | Start menu apps (`shell:AppsFolder`), shell icons and thumbnails (`IShellItemImageFactory` + `GetDIBits`). |
| `Services/KeyboardWatcher.cs` | Caps/Num Lock and the paste shortcuts through Raw Input (`RIDEV_INPUTSINK`). |
| `Services/FocusService.cs` | Do Not Disturb through undocumented WNF (see Decisions). |
| `Services/ClipboardWatcher.cs` | Copy alerts (`AddClipboardFormatListener`); skips content marked `ExcludeClipboardContentFromMonitorProcessing`. |
| `Services/PrivacyService.cs` | Mic in use (Core Audio capture sessions) and camera in use (`MFCreateSensorActivityMonitor`). |
| `Services/StatusService.cs` | Mic mute, Energy Saver, Wi-Fi / VPN / offline. |
| `Services/AudioMeter.cs` | Output peak levels for the real waveform. |
| `Services/StopwatchService.cs`, `AlarmService.cs`, `TimerService.cs` | Activities. |
| `Services/WeatherService.cs` | Open-Meteo forecast and geocoding, Windows `Geolocator`. |
| `Services/ShelfService.cs` | File shelf list (paths, persisted). The drag UI is in `MainWindow`. |
| `Services/NotificationService.cs` | Other apps' notifications (`UserNotificationListener`); needs identity. |
| `Services/CalendarService.cs` | iCal links (Ical.Net) and the Windows `AppointmentStore`, plus reminders. |
| `Services/PackageIdentity.cs`, `Identity/` | Identity check, the sparse package manifest and its setup script. |
| `Services/Hotkey.cs` | `RegisterHotKey` wrapper (default Ctrl+Alt+I hides/shows the island). |
| Other services | Volume, Brightness (WMI), Battery, VolumeKeyHook, StartupManager (HKCU Run value `DIsland`). |
| `Services/NotesService.cs` | To-do list and notes (`notes.json`); the pages and typing are in `MainWindow`. |
| `WhatsNewWindow.cs` | Per-version change list (`WhatsNew.Releases`) and the window shown after an update. |
| `Services/UpdateService.cs` | In-app updater: checks GitHub's latest release, downloads, verifies the SHA-256 and installs silently (see Decisions). |
| `installer/`, `tools/build-installer.ps1` | Inno Setup installer, guide, wizard art generation, `.sha256` file. |
| `LICENSE.txt`, `NOTICE.txt`, `PRIVACY.txt`, `THIRD-PARTY-NOTICES.txt` | The D-Island License 1.0; required credit, official-distribution and trademark notice; privacy statement; third-party licenses. **Keep `PRIVACY.txt` in sync with any new data source.** Write these one line per paragraph (the installer re-wraps text). |
| `CLA.md`, `.github/pull_request_template.md` | Contributor License Agreement, accepted through the PR checkbox. |

### How `MainWindow` works

- **Window:** a layered WPF window (`AllowsTransparency`) with `WS_EX_TOOLWINDOW` and
  `WS_EX_NOACTIVATE`; `WM_MOUSEACTIVATE` is answered with `MA_NOACTIVATE`. Fully transparent pixels
  pass clicks through, so the island's background alpha is never below 2/255.
- **Coordinates:** layout is in design units, 460 wide. The `Root` is scaled by the Size setting.
  The window is 90 units tall when collapsed. Expanded, it's at least 300 and grows (never shrinks)
  in 20-unit steps to fit a page (`UpdateWindowHeight`), because resizing rebuilds the GPU blur's
  layers.
- **Shape:** `PillShape` is rebuilt every frame from three springs (width, height, radius); notch
  mode adds concave shoulders. `ContentLayer`, `GlassLayer` and `GlassSheen` share the clip.
- **Views and pages:**
  - Collapsed views switch with a fade and scale (`SwitchTo`), driven by `StateController.Changed`
    → `Render()`.
  - The expanded island has pages: Home, Notifications, Calendar, Shelf, Weather, Apps and
    Wallpaper (`ExpandedPage`, `ShowPage`). While a page slides out, it has a negative bottom
    margin so the island sizes to the incoming page.
  - `MeasureExpandedHeight` calls `UpdateLayout()` first, because content added deep inside a page
    doesn't invalidate its ancestors' cached size.
- **Alerts:** the short ones share `InfoView` through `ShowInfo`; `CanShowTransient` prevents a
  blocked alert from overwriting the view. Expanding while an alert shows opens its page
  (`_alertPage`).
- **Theme:** `ApplyTheme()` is de-duplicated by a key. The text shadow is on the visible view only,
  and suspended while animating (re-blurring it every frame was about a third of the animation
  cost).
- **Z-order:** `KeepOnTop()` keeps the island at the top of the topmost layer, with the blur window
  right beneath it. Don't use owner/owned windows: re-owning a shown window left Windows' topmost
  bookkeeping inconsistent, and the island fell behind activated apps.
- **Startup:** the first frame comes first. Secondary services start 1.2 s later, one per dispatcher
  yield (`StartSecondaryServicesAsync`).

## Decisions and their evidence

- **Liquid Glass approaches that failed:**
  - `ACCENT_ENABLE_BLURBEHIND` drew black over the whole window rectangle.
  - System backdrops (acrylic) ignore window regions and go flat on inactive windows, and the
    island is never active.
  - `HostBackdropBrush` showed only its fallback, for the same reason.
- **Screen-capture engine (fallback):** `WDA_EXCLUDEFROMCAPTURE`, then GDI `BitBlt`, then a WPF
  `BlurEffect`, at 33 ms while animating and 66 ms otherwise. It hides the island from screenshots
  and costs CPU.
- **GPU engine (the default under Auto):**
  - **How it works:** the private `DwmpCreateSharedThumbnailVisual` (dwmapi ordinal 147) turns each
    window behind the island into a live `IDCompositionVisual`. They're blurred with
    `IDCompositionGaussianBlurEffect` (standard deviation = radius / 3) and clipped. The
    DirectComposition device has no rendering device (`DCompositionCreateDevice3(null)`), which
    saves a driver instance.
  - **Source:** ADeltaX, "DWM Thumbnails. But with IDCompositionVisual".
  - **Probe:** the engine is tested in a child process first, and the result cached as
    `GpuBlurProbe = "v4|<build>.<UBR>:ok|fail"`. Bump `v4` when the API usage changes.
  - **Pitfalls:**
    - Thumbnail rectangles must lie inside the host window (else `E_INVALIDARG`).
    - `DwmUpdateThumbnailProperties` doesn't affect these visuals, so a moved window gets a new
      thumbnail.
    - `AddVisual(v, insertAbove: true, null)` puts `v` *behind* its siblings.
    - `DwmpCreateSharedMultiWindowVisual` (ordinals 163/164) succeeds but draws nothing.
  - **WinUI 3 windows (Apple Music, the new Notepad) come out empty, whatever you try:** the
    top-level window and its `DesktopChildSiteBridge` child are both accepted (hr=0) but draw
    nothing with flags 0 and 2, and flags 1, 3 and 4 draw nothing at all. So **Auto switches to the
    capture engine while a `WinUIDesktopWin32WindowClass` window is the top window behind the
    island** (`UpdateWinUiBehind`).
- **Keep WPF on the GPU:** software rendering saved ~38 MB but used ~58% more CPU while animating.
- **Tiered PGO is off** (csproj). It re-JITs for ~0.6 s of CPU after each launch and made animation
  no faster (35.4% vs 30.9%). Turning tiered compilation off entirely saves more CPU but delays the
  first frame by ~200 ms. ReadyToRun didn't help.
- **Energy Saver mode:** under Windows Energy Saver, animations cap at 30 fps and the bars at half
  rate (animating 31.4% → 25.8%).
- **Shell icons:**
  - The bitmaps have **straight** alpha, not premultiplied. Photo thumbnails have all-zero alpha,
    so treat them as opaque.
  - For Store apps, `SIIGBF_ICONONLY` crops the logo edge to edge, so use flags 0 and trim the
    transparent margin (`FitLogo`).
  - Draw icons 1:1 at a standard icon size to keep them sharp.
- **Wheel and touchpad** (`OnRootPreviewMouseWheel`): a gesture that scrolled a list never falls
  through into a page flip.
  - **Mouse** (delta a multiple of 120): one page per notch, ignoring flips closer than 100 ms. A
    new gesture starts after a 300 ms gap.
  - **Touchpad:** one page per swipe. A new swipe starts after a gap over 220 ms, a direction
    change, or a speed jump (≥2× the average and ≥30), but only after the momentum has faded below
    40% of the swipe's peak, and not within 250 ms of a flip.
  - **Earlier versions failed:** one flipped twice per real swipe, because the fast ramp-up looked
    like a new swipe; another dropped quick flicks.
  - **Testing:** use a realistic swipe profile: a fast ramp, a peak around 100, then ~30 decaying
    events 8 ms apart.
  - **Lists:** WPF scrolls a fixed 48 px per wheel message, too fast on precision touchpads.
    `WheelScrolling` scrolls by the delta instead.
- **Do Not Disturb** reads the WNF state `WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED`
  (`0x0D83063EA3BF1C75`). 0 means off and 1 is the user's Do Not Disturb. **2 is Windows' automatic
  quiet mode**, used while the Snipping Tool overlay is open after PrtSc and for full-screen apps.
  Only profile 1 is announced; notifications are held back for any profile.
- **Mic and camera:** the registry's `CapabilityAccessManager\ConsentStore` usage times **stopped
  updating** on build 26200; they moved to an admin-only database. Use live sources instead:
  - **Mic:** Core Audio capture sessions on every endpoint, with session callbacks.
  - **Camera:** Media Foundation's sensor activity monitor.
  - **Naming:** apps that capture through a WebView2 child are named after their host app.
- **Raw Input, not a low-level keyboard hook:** a busy UI thread can't delay the user's typing.
  `GetKeyState` gives the correct toggle state from the background.
- **Package identity** (notifications, Windows calendar):
  - **Package:** `Identity\AppxManifest.xml` is a *sparse* package (`AllowExternalContent`,
    `TrustLevel=mediumIL`, `RuntimeBehavior=win32App`, `AppListEntry=none`).
  - **Linking:** `app.manifest` links the exe through `<msix publisher="CN=D-Island Dev"
    packageName="DIsland" applicationId="DIsland">`. Windows matches that element, so Debug builds
    get the identity too.
  - **Setup:** `setup-identity.ps1` gets makeappx and signtool from the
    `Microsoft.Windows.SDK.BuildTools` NuGet package, creates a self-signed certificate (trusting it
    is the only elevated step), packs with `/nv`, signs, and runs `Add-AppxPackage
    -ExternalLocation <exe dir>`.
  - **Side effect:** with identity, Windows asks for location consent per app.
- **Calendar:** Ical.Net 4.3.1's `AsSystemLocal` ignores TZID, so convert with
  `AsUtc.ToLocalTime()`. The service loads the whole current month (for the month grid's dots) plus
  the next week.
- **Bluetooth battery:** device property `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2` on a device in
  the container.
- **Apple Music motion artwork** (opt-in, unofficial):
  - **Lookup:** iTunes Search API. **Token:** music.apple.com `/us/browse` → `/assets/index*.js` →
    a JWT (`kid=WebPlayKid`).
  - **Catalog:** `amp-api.music.apple.com/v1/catalog/{cc}/albums/{id}?extend=editorialVideo`.
  - **Download:** the smallest H.264 variant ≥480 px. It's fragmented MP4, which `MediaElement`
    won't play, so it's transcoded once to MP4 at 15 fps (15 fps ≈17% CPU expanded, 24 fps ≈27%).
  - **May break whenever Apple changes the web player.**
- **License:** from 1.1.0, the custom, source-available D-Island License 1.0 (not OSI-approved).
  - **Terms:** visible credit ("Based on D-Island by h7 (ibuildrigs) - https://github.com/H7gamer/d-island"),
    no malware or monitoring without consent, and modified versions need a different name and icon.
  - **Contributions:** under `CLA.md`, so the maintainer can relicense them.
  - **Earlier copies:** 1.0.0 was Apache 2.0, and copies already obtained stay Apache. Its
    commits, tag and release were removed (the history now starts at 1.1.0).
  - **Source headers:** every source file starts with the two-line D-Island License header.
- **Updater** (`UpdateService`, Settings > Updates, both options on by default):
  - **When:** checks once a day, the first time 1 minute after start, using
    `api.github.com/repos/H7gamer/d-island/releases/latest`; drafts and prereleases are skipped.
  - **What it accepts:** only a `D-Island-Setup-<x.y.z>.exe` asset plus its `.sha256` asset, both
    under `https://github.com/H7gamer/d-island/releases/download/`.
  - **Download:** to `%LocalAppData%\DIsland\Updates`. It fails after 30 s without data, caps the
    size at 400 MB, and deletes the file on a checksum mismatch.
  - **Install:** only when the app runs from an installation (`unins000.exe` next to the exe) and
    the island is idle: no timer, stopwatch or alarm, collapsed, no alert, not animating. Otherwise
    it retries every 10 minutes. It runs `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART
    /MERGETASKS="!identity"`. The installer's `[Run]` entry with `Check: WizardSilent` relaunches
    the island, which then shows "D-Island updated" (`LastRunVersion`).
  - **Static fields:** `CurrentVersion` must stay declared above `Http`. Static fields initialise in
    order, and the client's User-Agent uses the version.
  - **No SmartScreen prompt on updates:** the updater downloads with `HttpClient`, so the installer
    has no Mark of the Web, and SmartScreen only ever checks the first, browser-downloaded install.
- **What's New** (`WhatsNewWindow.cs`): shown once on the first start of a newer version
  (`LastRunVersion` < current), automatic or manual update alike, never on a fresh install; also
  from Settings > Updates. **Every release needs an entry in `WhatsNew.Releases`** (and the "What's
  new" part of `.github/release-notes.md`). Without one, the window falls back to the latest entry.
- **Shape morph and hide/show:** a fourth spring, `_notch` (0 island … 1 notch, critically damped),
  drives `PillTop` and `BuildNotch(…, t)` in three phases: slide up to the edge, square the top
  corners, flare the shoulders. The GPU blur's clip is one rounded rectangle, so its top corners
  may only square off once the shape touches the edge, because the clip's square top then hangs
  above the window. Hiding springs the pill to 0 × 0 and fades `Root`, then hides the window
  once settled (`FinishHide`); showing grows it back from 0.
- **Typing in the island** (To-Do, Notes): the window is `WS_EX_NOACTIVATE`, so it can't get
  the keyboard. `BeginTyping` clears that style and calls `SetForegroundWindow`, which Windows
  allows because this process just got the click. `EndTyping` restores the style and returns
  focus to the previous foreground window (Esc, collapse, page switch, hide), or lets go when the
  window is deactivated. While typing, the island doesn't auto-collapse and copy/paste alerts are
  off. Clicks on these pages are marked handled so they don't collapse the island. Data:
  `NotesService`, `%LocalAppData%\DIsland\notes.json`, written through a temporary file;
  typing is saved after 500 ms.
- **Hide shortcut when another app owns it:** `RegisterHotKey` is first come, first served. On
  failure the same combination is matched from raw input (`KeyboardWatcher.Shortcut`). When
  another app has the hotkey, Windows swallows the key-down entirely, and raw input sees only the
  key-up, so the fallback fires on a key-up whose key-down never arrived. Modifiers are tracked
  from the raw stream: `GetAsyncKeyState` is already "up" when quickly pressed keys are
  processed. An entry also counts only while Windows agrees it's down, or for 500 ms, so a missed
  key-up (Win+L) can't leave it stuck.
- **Weather place name:** with "My location", the town comes from OpenStreetMap Nominatim reverse
  geocoding (same 2-decimal coordinates, `zoom=10`). It's asked once per place and cached in
  settings (`WeatherDevicePlace` and its key). The User-Agent names the app and repository, as
  Nominatim's policy requires, and the page credits OpenStreetMap whenever it shows the name.
- **Code signing (optional):** `build-installer.ps1` signs the app, setup and uninstaller when
  `DISLAND_SIGN_PFX` or `DISLAND_SIGN_PFX_BASE64` (plus `DISLAND_SIGN_PASSWORD`) is set. The
  release workflow maps them from the repository secrets `SIGN_PFX_BASE64` and
  `SIGN_PFX_PASSWORD`. The certificate is imported for the build and used by thumbprint, because
  Inno Setup echoes the sign command into the log, which must not show the password. Tested with
  a throwaway self-signed certificate.

## Measurements (development machine, Debug unless noted, 3-run averages)

| State | Result |
|---|---|
| Idle, collapsed | ~0.2–0.9% of one core |
| Continuous expand/collapse | ~25.5% on AC; ~31% on battery (optimised build: ~31%, ~26% in Energy Saver mode) |
| Expanded, static | ~3.7% |
| Expanded with motion artwork | ~17% |
| Private memory | ~120 MB; the managed heap is ~2.5 MB, and the rest is driver and WPF state |
| Startup CPU (first 8 s, Release) | ~5.1 s, of which ~0.6 s is reading existing notifications |

## Installer

`tools\build-installer.ps1` does the following:
- generates `DIsland.ico` and the wizard art,
- runs a self-contained `dotnet publish` to `artifacts\publish`,
- compiles `installer\DIsland.iss` with Inno Setup 6, producing `artifacts\D-Island-Setup-<v>.exe`,
- writes `artifacts\D-Island-Setup-<v>.exe.sha256`.

**Releasing** (GitHub Actions, `.github/workflows/release.yml`):
1. Bump `<Version>` in `DIsland.csproj`. Add the version's `WhatsNew.Releases` entry, and update
   the "What's new" part of `.github/release-notes.md` and `PRIVACY.txt` if anything changed.
2. Commit and push `main`, then push the tag `v<x.y.z>`, which must match `<Version>`.
3. The workflow builds on a Windows runner and publishes the release with **both** the `.exe` and
   the `.sha256`. Without the checksum asset, the updater ignores the release.
4. Check that the release has both assets (the public API:
   `/repos/H7gamer/d-island/releases/latest`).

What the installer contains and does:
- **Wizard pages:** a per-user install with no admin; the license (`LICENSE.txt`) the user must
  accept; a privacy page; the how-to guide (`installer\HowToUse.rtf`).
- **Tasks:** start with Windows, a desktop shortcut, and the identity setup.
- **Uninstall:** removes the identity package and the signing key, and asks before deleting
  settings.
- **Tested:**
  - Silent install and uninstall into a scratch folder.
  - The silent install relaunches the island.
  - The updater, against a local feed: it installs when the checksum matches, and deletes the
    download and fails when it doesn't.
  - **Not tested:** a real update from GitHub, which needs two published releases.

## Open items and untested areas

- **Never seen on screen:** notch mode with the GPU engine; Size ≠ 100% with the GPU engine;
  multiple monitors (the island is primary-monitor only); dragging windows behind the island.
- **Not exercised with real hardware or events:** device plug-in events (only `DISLAND_DEVICE`),
  brightness keys, the volume-flyout hook, battery plug/unplug, alarms ringing, status alerts,
  earbud battery on connect, real OLE drag in and out of the shelf, the Windows calendar with real
  events, motion artwork with Apple Music actually playing.
- **Known limits:**
  - DRM video shows black in the GPU blur, which mirrors at most 24 windows.
  - Wallpaper Engine pause/mute state is tracked locally.
  - Other always-on-top islands (e.g. Windhawk's) fight for z-order.
  - Spotify Canvas is unsupported.
  - Calls and airplane mode aren't implemented: airplane mode needs the `radios` capability.
- **Possible next steps:**
  - Render the island with DirectComposition instead of a WPF layered window, to remove the
    per-frame readback.
  - Get a code-signing certificate and add it as the `SIGN_PFX_BASE64` / `SIGN_PFX_PASSWORD`
    secrets, so SmartScreen stops warning on first installs (the signing pipeline is ready).

## Conventions

- **Comments:** keep them in the existing style. Short summaries are the norm, plus "why" comments
  for Windows quirks.
- **Verification:**
  - Build after every change: 0 warnings.
  - Verify visual changes with screenshots or snapshots.
  - Measure optimisations with `tools\bench.ps1`.
- **Privacy:**
  - No telemetry.
  - Every new data source goes into `PRIVACY.txt`.
  - Back up users' settings in tests.
- **Commits:** authored by the maintainer (h7). Credit for AI assistance goes in the README's
  Contributors section.

# D-Island

A Dynamic Island–style overlay for Windows 10/11 (WPF, .NET 8): music, timers, notifications,
calendar, weather, a file shelf and system alerts in a pill at the top of your screen.

Built by h7 · [ibuildrigs.com](https://ibuildrigs.com) · Licensed under the [D-Island License](LICENSE.txt)

> [!IMPORTANT]
> **Official downloads are only published here, on
> [GitHub Releases](https://github.com/H7gamer/d-island/releases), and on
> [ibuildrigs.com](https://ibuildrigs.com).** Copies downloaded anywhere else (other repositories,
> forks, mirrors, download sites, app stores) are not authorised by h7 and may have been modified.
> D-Island can read notifications and the clipboard, so a tampered copy could be dangerous: if you
> install one, you do so at your own risk. See [NOTICE.txt](NOTICE.txt).

## License and privacy

From version 1.1.0, D-Island is **source-available** under the [D-Island License](LICENSE.txt).
You may use it (including commercially), study it, modify it and redistribute it, as long as you:

- **Credit the author visibly.** Show *"Based on D-Island by h7 (ibuildrigs) -
  https://github.com/H7gamer/d-island"* in your software's About, credits or settings screen, and
  keep the license files and the copyright notices in the source.
- **Keep users informed and in control.** Ship an accurate privacy statement, and ask for consent
  before collecting anything the official version doesn't.
- **Never use it to harm or deceive.** No malware, spyware or stalkerware, no monitoring people
  without their consent, no bypassing privacy protections, no impersonation.
- **Don't present a modified version as official.** Use a different name and icon.

Breaking these terms ends your rights under the license. Versions before 1.1.0 were released under
the Apache License 2.0, and copies of those versions stay under it. Because of these conditions,
the D-Island License is not an OSI-approved open-source license.

[PRIVACY.txt](PRIVACY.txt) lists everything the island reads, what it sends over the internet
(weather, your calendar links, optional Apple artwork) and what it stores. There's no telemetry.
Open-source components keep their own licenses: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

D-Island is an independent project, not affiliated with Apple or Microsoft.

## Contributors

- **h7** ([@H7gamer](https://github.com/H7gamer)): creator and maintainer
- **Claude** (Anthropic): AI pair programmer, who helped design and write much of the code

Contributions are welcome under the [Contributor License Agreement](CLA.md); see
[CONTRIBUTING.md](CONTRIBUTING.md). Report security problems
privately as described in [SECURITY.md](SECURITY.md).

## Install

Download `D-Island-Setup-<version>.exe` from
[Releases](https://github.com/H7gamer/d-island/releases) and run it. It installs for the current user (no admin rights needed) into
`%LocalAppData%\Programs\D-Island`, includes .NET, and shows a short "How to use D-Island" guide
before installing (also in the Start menu afterwards). Options: start with Windows, a desktop
shortcut, and the one-time notifications / Windows calendar setup. Uninstall from Settings > Apps;
it also removes the identity package and asks whether to delete your settings.

To build the installer: `.\tools\build-installer.ps1` (needs Inno Setup 6:
`winget install JRSoftware.InnoSetup --scope user`). Output: `artifacts\D-Island-Setup-<version>.exe`.

## Build and run

```
dotnet build
dotnet run
```

Double-click the tray icon, or right-click it and choose **Settings…**, to open the settings window. Changes apply live.
The tray menu also has **Hide island**, **Hide Windows volume flyout**, **Start with Windows** and **Exit**.
**Ctrl+Alt+I** hides and shows the island from anywhere (change it in Settings > System).

## Shape and material

- **Shape:** Dynamic Island (default) is a floating pill just below the top edge. Notch is attached to
  the top edge with flared shoulders, like a laptop notch.
- **Material:**
  - **Opaque** (default): a solid background.
  - **Translucent:** a see-through background with adjustable opacity.
  - **Liquid Glass:** a live blur of whatever is behind the island, with a light tint and adjustable
    shininess (edge highlight and top sheen). The Blur slider sets the radius, from Clear to Frosted.
    There are two blur engines (Settings → Material → Blur engine):
    - **GPU** (used by Auto when available): for each window behind the island, wallpaper included,
      Windows' window manager provides a live thumbnail as a DirectComposition visual. The island
      stacks them in screen order, blurs them with a DirectComposition Gaussian blur and clips them
      to its shape in a window just beneath it. Nothing is copied to the CPU, and the island's own
      windows are never added, so it stays visible in screenshots and recordings. This relies on an
      undocumented Windows function (`DwmpCreateSharedThumbnailVisual`, dwmapi ordinal 147), so the
      first time it runs on each Windows build it is tested in a separate child process
      (`DIsland.exe --probe-gpu-blur`). A failure can only crash that child process, never the
      island. The result is cached per build, revision included, so Windows updates are re-tested.
    - **Screen capture** (fallback): copies the screen just behind the island at 15–30 fps and blurs
      it. This works everywhere, but it uses more CPU and hides the island from screenshots while
      blurring.
- **Adaptive background** (on by default): when only the desktop or wallpaper is behind the island,
  the material fades to the "opacity over desktop" level (15% by default). When any app window sits
  behind it, the chosen material is used in full. Text and icons always stay fully opaque. While
  expanded, the full material is used by default so the island stays readable.

## Media apps

The island works with any app that publishes to Windows' media controls (GSMTC), including Spotify,
Apple Music, browsers and Media Player. It shows the title, artist, album, artwork, the player's name
in its accent color, and a progress bar you can click to seek when the app supports it. When several
apps have sessions, the one that is actually playing wins.

Artwork comes from the session thumbnail. It is shown as a banner behind the expanded media view and
animates when the app supplies an animated GIF.

### Animated album art (Apple Music motion artwork)

This is opt-in, under Settings → Media → "Animated album art from Apple Music", and off by default.
Windows doesn't expose motion artwork, and Apple's official Music API doesn't give it to third-party
apps, so this uses the same unofficial route as open-source downloaders:

1. **Find the album:** the playing artist and album (or title) are looked up with Apple's public
   iTunes Search API. This works from any player, including Spotify, if the album is on Apple Music.
2. **Get a token:** the anonymous token embedded in the music.apple.com web player (JWT `kid`
   `WebPlayKid`) is read from its JavaScript bundle and cached until it expires.
3. **Get the stream:** the album's square motion artwork comes from
   `amp-api.music.apple.com/v1/catalog/{country}/albums/{id}?extend=editorialVideo`, as an HLS stream
   (`editorialVideo.motionDetailSquare`).
4. **Download:** the smallest H.264 variant at least 480 px wide is downloaded. HEVC variants are
   skipped because they need a codec extension on Windows.
5. **Convert:** Windows' built-in, hardware-accelerated transcoder converts it once into a regular
   15 fps MP4. WPF's player won't play the fragmented original, and 15 fps measured about 17% of a
   core while playing, against about 27% at 24 fps.
6. **Play:** the loop plays muted in the banner and the large artwork square, only while the island is
   expanded.

Every result is cached in `%LocalAppData%\DIsland\MotionArt`, up to 200 MB. "No motion artwork" is
remembered for 7 days. A typical fetch takes about 6 s and a cached album opens instantly. Only some
albums have motion artwork. Because this relies on Apple's private web-player endpoints, it can stop
working whenever Apple changes them. Spotify Canvas videos are not supported.

## What it shows

| State | Kind | Trigger |
|---|---|---|
| Clock | persistent (lowest) | idle |
| Media | persistent | a GSMTC session is playing |
| Timer | persistent (highest) | a countdown is running or paused |
| Volume / brightness OSD | transient (2 s by default) | Core Audio endpoint callback / `WmiMonitorBrightnessEvent` |
| Charging / on battery | transient (3 s by default) | AC line status change |
| Low (20%) / critical (10%) battery | transient (3 s by default) | crossing the threshold while discharging |
| Timer finished | transient, 5 s (click to dismiss) | countdown reached zero |
| Notification | transient (5 s by default) | another app's notification (needs the one-time setup below); held back during Do Not Disturb |
| Calendar reminder | transient | 5 minutes (configurable) before a timed event |
| Device connected / disconnected | transient | USB, Bluetooth or monitor (a `DeviceWatcher` over device containers) |
| Mic / camera in use | transient, plus an orange (mic) or green (camera) dot beside the pill while in use | Core Audio capture sessions / Media Foundation sensor activity |
| Caps Lock / Num Lock, Do Not Disturb | transient | Raw Input; Windows' quiet-hours state |
| Copied / pasted | transient | clipboard listener; Ctrl+V or Shift+Insert (password managers' private copies are never shown) |
| Stopwatch | persistent (between timer and media) | started from Home |
| Alarm | transient, rings until clicked (max 1 min) | an alarm from Settings > Alarms |
| Microphone muted, Energy Saver, Wi-Fi / VPN | transient | endpoint mute callback, PowerManager, NetworkInformation |
| Second activity | a bubble beside the pill | two activities at once (e.g. music while a timer runs), like the iPhone's split island |

Bluetooth devices that report it show their battery in the connect alert (`Connected · 80%`), and
the music bars follow the actual sound.

The idle clock can show the weather next to it: `10:58 PM | ☁ 26°`.

Hovering or clicking expands the island. Moving the pointer away or clicking again collapses it.
Both delays can be changed in settings. Clicking or hovering while an alert is showing opens that
alert's page.

## Pages

The expanded island has pages. Switch with the tabs (icons, or page dots; see Settings), or with the
mouse wheel / touchpad over the island. A scroll that reaches the end of a list stops there; scroll
again in the same direction to move to the next page.

| Page | Shows |
|---|---|
| Home | Now playing (artwork, progress, controls) or a large clock; timer presets / the running timer |
| Notifications | Other apps' notifications, newest first. Click one to open its app, ✕ to dismiss it, or Clear all |
| Calendar | This month's dates (today highlighted, dots on busy days; click a day to list it) and the next 7 days, with "in 12 min", "Now" and **Join** for Teams / Zoom / Meet / Webex |
| Shelf | Files dragged onto the island, kept until you drag them out somewhere else. The shelf stores links, not copies |
| Weather | Now, the next 12 hours and 5 days, from Open-Meteo (free, no account), for Windows' location or a chosen city |
| Apps | Up to 16 quick-launch apps picked from the Start menu |
| Wallpaper | Wallpaper Engine controls (when it is running) |

## Notifications and the Windows calendar (one-time setup)

Windows only lets apps read notifications and the Windows calendar if they have a *package identity*.
**Settings > Notifications & Windows calendar > Set up…** gives D-Island one by registering a sparse
package next to the exe (`Identity\setup-identity.ps1`). It:

1. downloads Microsoft's packaging tools (the `Microsoft.Windows.SDK.BuildTools` NuGet package, about 100 MB, once),
2. creates a self-signed certificate "CN=D-Island Dev",
3. trusts it on this PC (Windows asks for permission once: the only step that needs admin), and
4. registers the package with D-Island's folder, then restarts the island.

Nothing is installed or moved. Run it again if you move the app to another folder. To undo:
`Get-AppxPackage DIsland | Remove-AppxPackage`, then remove the certificate in `certlm.msc` (Trusted
People) and `certmgr.msc` (Personal).

Calendars from iCal links (Google "Secret address in iCal format", Outlook "ICS", iCloud public
links) work without the setup.

## Wallpaper Engine

When Wallpaper Engine is running:

- **Theme** (accent source Auto or Wallpaper): the island takes a dark tint of the current wallpaper's color, and the volume/brightness
  bars, song progress bar and equalizer use that color. It comes from the wallpaper's scheme color in
  `project.json`. When that is unset (black or white), the strongest color in its preview image is
  used instead. The theme follows wallpaper changes by watching Wallpaper Engine's `config.json`.
- **Controls:** the expanded island shows the wallpaper's (animated) preview and title, with pause,
  mute and next-wallpaper buttons. These use Wallpaper Engine's `-control` command line, and Next
  only works when a playlist is active.
- **Optional:** mute the wallpaper while music is playing. It is unmuted afterwards, but only if the
  island was the one that muted it.
- The wallpaper is never shown as "now playing", and the island stops its own animations when they
  are off-screen so it doesn't compete with the wallpaper for the GPU.

## Settings

Settings are stored in `%AppData%\DIsland\settings.json` and can be edited in the settings window:

| Group | Setting | Default |
|---|---|---|
| Style | Shape | Dynamic Island (or Notch) |
| | Size | 100% (75–150%) |
| | Corner roundness | 100% |
| | Outline width / opacity | off / 25% |
| | Shadow | off |
| Material | Opaque / Translucent / Liquid Glass | Opaque |
| | Translucent opacity | 70% |
| | Glass blur / shininess / tint | 35% / 30% / 12% |
| | Blur engine | Auto (GPU when verified, else screen capture) |
| | Text shadow on see-through materials | on |
| Adaptive background | Fade over the desktop | on |
| | Opacity over desktop | 15% |
| | Full material while expanded | on |
| Colors | Accent: Auto, Wallpaper, Media app, Custom or Mono | Auto |
| | Custom color | #0A84FF (swatches or any hex code) |
| | Background tint | 18% |
| Text & clock | Font | Segoe UI Variable (or Segoe UI, Bahnschrift, Cascadia Mono) |
| | Clock format | System (or 12-hour, 24-hour) |
| | Seconds / date in idle pill | off / off |
| | When idle | Show clock (or a minimal empty pill) |
| Interaction | Expand on hover | on (clicking always works) |
| | Hover delay / collapse delay | 120 ms / 250 ms |
| Animation | Speed / bounciness | 1.0× / 35% |
| Media | Album art banner | on |
| | Animated album art from Apple Music | off (unofficial; see above) |
| Wallpaper Engine | Wallpaper controls / mute while music plays | on / off |
| Expanded island | Navigation: tabs or page dots | Tabs |
| | Mic / camera indicator | on |
| Notifications & Windows calendar | Show notifications / hide their text | on / off |
| | Notification alert duration | 5 s |
| Calendar | Calendar page / Windows calendars / reminder | on / on / 5 min |
| | iCal links | none |
| File shelf | Shelf page / keep files after dragging out | on / off |
| Weather | Weather page / location / units | on / My location / Auto |
| | Weather next to the clock | on |
| Quick apps | Apps / icon size | File Explorer, Settings / 26 px |
| Alerts | Device, Caps/Num Lock, Do Not Disturb, copied, pasted | all on (copied-text preview on) |
| | Volume/brightness / battery and device durations | 2 s / 3 s |
| Position | Top offset | 0 px |
| System | Hide / show shortcut | Ctrl+Alt+I |
| | Hide Windows volume flyout | off (a keyboard hook takes over the volume keys) |
| | Start with Windows | off |

**Reset to defaults** restores the look-and-feel settings. It leaves the volume flyout, wallpaper
muting and startup settings alone.

## Performance

The island does almost no work while nothing on screen changes:

- **Event-driven:**
  - Window moves, z-order, show/hide, minimize and cloak changes arrive through WinEvent hooks
    (`WindowEventWatcher`, debounced to 30 ms). They drive both the GPU blur and the
    app-behind/desktop detection. Polling is only a 5 s safety net.
  - Wallpaper Engine's exit is a process event, and wallpaper changes are picked up by a file
    watcher on `config.json`. Process enumeration only happens while Wallpaper Engine isn't running.
  - Battery changes come from power broadcasts, with a 15 s safety poll.
  - Media timeline updates only move the progress bar. They don't re-query metadata or artwork.
  - Notifications use Windows' live change event; devices, mic/camera, clipboard, Do Not Disturb,
    lock keys and the Windows calendar are all callbacks. Calendar reminders wake exactly when one
    is due. Weather refreshes every 20 minutes and iCal links every 10.
- **On demand:**
  - The clock wakes once a minute (once a second only when seconds are shown).
  - The theme is re-applied only when something that affects it changes.
  - The spring animation loop runs only while the island is moving.
  - The equalizer and animated thumbnails run only while visible.
  - The progress and leave timers run only while expanded.
- **Runtime:** no background GC thread. Tiered PGO is off: it re-compiled for about 0.6 s of CPU
  after every launch and didn't make animation faster.
- **Startup:** the first frame comes first; the other services (devices, mic/camera,
  notifications, calendar, weather, …) start a moment later, one at a time.
- **Energy Saver:** while Windows' Energy Saver is on, the island animates at 30 fps instead of 60
  and the music bars refresh at half rate (about 18% less CPU while animating).
- **Rendering:**
  - **Short window when collapsed:** a transparent WPF window is redrawn and copied to the screen
    in full on every animation frame. While collapsed the window is 90 DIPs tall instead of 300, and
    the GPU blur area follows it.
  - **Text shadow only when settled:** the shadow sits on the visible view only and is dropped while
    views animate. Re-blurring it every frame measured about a third of the animation cost.
  - **Frame caps:** island animations are capped at 60 fps (it matters on 120/144 Hz displays) and
    the equalizer runs at 30 fps.
  - **GPU rendering kept:** software rendering saved about 38 MB of memory but used about 58% more
    CPU while animating.
- **Memory:**
  - The GPU blur uses DirectComposition without a Direct3D device, which avoids a second
    graphics-driver instance.
  - Artwork is decoded no larger than it is shown: 480 px for album art, 128 px for wallpaper
    previews. Animated GIF frames are composited at that size and capped at 32 MB in total.

Measured on the development machine (Windows 11 26200, Intel UHD, 60 Hz, Debug build, Liquid Glass
on the GPU engine). Animating means the island expanding and collapsing nonstop; each figure is the
average of 3 runs:

| | Before optimising | After | With all features (2026-10-19) |
|---|---|---|---|
| Idle CPU | ~0.3–0.8% of one core | ~0.2–0.5% | ~0.2–0.9% |
| Animating CPU | ~31.5% | ~25.5% | ~25.8% |
| Private memory | ~150 MB | ~118 MB | ~120 MB |

The .NET heap is only about 2.5 MB live. Most of the memory is graphics-driver and WPF rendering
state.

## Layout

- `MainWindow.xaml(.cs)`: window styles, spring-animated pill and notch geometry, view switching, hover/click handling.
- `SettingsWindow.xaml(.cs)`: the live settings editor; sections are generated in code.
- `GpuBlurBackdrop.cs`: the GPU Liquid Glass engine (DWM thumbnail visuals, DirectComposition blur and clip).
- `Services/ScreenCapture.cs`: the screen-capture fallback engine.
- `Services/WindowEventWatcher.cs`: debounced WinEvent hooks for top-level window changes.
- `Services/MotionArtworkService.cs`: Apple Music motion artwork lookup, download, conversion and cache.
- `Services/BackgroundDetector.cs`: detects whether an app window or only the desktop is behind the island.
- `StateController.cs`: priority logic for transient and persistent states.
- `Spring.cs`: damped spring used for width, height and corner radius.
- `Services/NotificationService.cs`, `CalendarService.cs`, `PackageIdentity.cs` and `Identity/`: notifications, calendar and the sparse-package setup.
- `Services/WeatherService.cs` and `WeatherIcons.cs`: Open-Meteo weather and the drawn weather icons.
- `Services/ShelfService.cs`, `QuickAppsService.cs`: the file shelf; Start menu apps, icons and thumbnails.
- `Services/DeviceService.cs`, `PrivacyService.cs`, `KeyboardWatcher.cs`, `FocusService.cs`, `ClipboardWatcher.cs`: alert sources.
- `Services/Hotkey.cs`, `WheelScrolling.cs`: the hide/show shortcut; proportional wheel scrolling.
- `Services/`: also media, volume, brightness, battery, timer, the volume-key hook, settings and startup registration.
- `Interop/`: P/Invoke and Core Audio COM definitions.

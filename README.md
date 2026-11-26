# Rosella

A heavily opinionated desktop environment, including its own compositor /
display server, written in C# on .NET 10. The point is to learn how desktop
environments and display servers actually work by building one, from the wire
protocol up.

Rosella is **not** a Mutter/GNOME Shell plugin and does not link libwayland.
It speaks the Wayland wire protocol itself in managed code, and talks to the
machine through the usual Linux graphics/input ABI seams:

- **DRM/KMS** (kernel) — modesetting, page flips, scanout
- **Mesa** (GBM/EGL, GL or Vulkan) — GPU buffers and rendering, measured by
  `tools/Rosella.GpuProbe`; the compositor itself still renders on the CPU
- **evdev** (kernel) — input devices, read directly (no libinput)

See `docs/architecture.md` for the layer map, the wire-format facts the codec
is built on.

## Design stance

Opinionated by design: almost no configurable settings. Defaults are chosen
and hardcoded; launcher configuration is a small exception for machines with
different installed applications.

## Features

- Floating dock with pinned launchers, running app icons, and live taskbar reload.
- Quick Settings for audio, display resolution, and session controls.
- Maximised windows by default, fullscreen support, and an Alt+Tab switcher.
- Wayland clipboard and drag and drop, region screenshots, and notifications.
- X11 app support through rootless XWayland, including Steam, when the helper is installed.

## Layout

```
src/Rosella.Protocol/          Wayland wire format + protocol description (embedded wayland.xml)
src/Rosella.Transport/         unix sockets, recvmsg/sendmsg, SCM_RIGHTS descriptor passing
src/Rosella.Server/            display server: sessions, object maps, dispatch, globals
src/Rosella.Drm/               DRM/KMS interop: devices, connectors, modes, dumb buffers, flips
src/Rosella.Compositor/        entry point: scanout loop, desktop background, --probe
scripts/                       login session registration, session wrapper, and the `rosella` VT launcher
tests/Rosella.Protocol.Tests/  unit tests, including golden wire bytes
tests/Rosella.Server.Tests/    transport + end-to-end handshake tests
tests/Rosella.Drm.Tests/       kernel ABI golden layouts, ioctl codes, event parsing
tests/Rosella.Compositor.Tests/shell chrome, input and flip matching
docs/architecture.md           design notes and roadmap
```

## Build & test

```sh
dotnet build
dotnet test tests/Rosella.Protocol.Tests/Rosella.Protocol.Tests.csproj
dotnet test tests/Rosella.Server.Tests/Rosella.Server.Tests.csproj
dotnet test tests/Rosella.Drm.Tests/Rosella.Drm.Tests.csproj
dotnet test tests/Rosella.Input.Tests/Rosella.Input.Tests.csproj
dotnet test tests/Rosella.Control.Tests/Rosella.Control.Tests.csproj
dotnet test tests/Rosella.Compositor.Tests/Rosella.Compositor.Tests.csproj
```

Note: `dotnet test` run against the `.slnx` at the repo root currently no-ops
(SDK 10.0.112) — invoke the test project directly as shown above.

Golden-byte tests assume a little-endian host (the wire format is native
byte order; Rosella targets x86_64/ARM64).

## Running Rosella

Two entry points, both running the same build.

### As a login session (the normal way)

`scripts/install-session.sh` publishes Rosella and registers it as a Wayland
session (`/usr/share/wayland-sessions/rosella.desktop` plus a wrapper in
`/usr/local/bin`), so "Rosella" appears in the login screen's session list
alongside GNOME. It needs sudo once; day-to-day updates do not
(`scripts/deploy.sh`, then restart). Run `deploy.sh` from
`~/Projects/Rosella`, the checkout both launchers use. If the installed
login launcher or session entry differs from the repository, `deploy.sh`
warns: refresh the registration with `scripts/install-session.sh` (which
requires sudo). Otherwise the login screen may continue to start an older
build.

With GDM, pick **Rosella** from the session menu. A greetd setup can use
[Rosella's greeter](docs/login-and-pin.md) with a PIN or password.

For X11 apps such as Steam, install `xwayland-satellite` 0.8.3 or later and
Xwayland. On Fedora, `sudo dnf install xwayland-satellite` installs both.
Rosella starts the bridge on login and gives apps launched from Rosella the
credentials for that desktop. The bridge uses GPU rendering when available.

### From a VT, alongside a running session (the dev way)

The seat is single-occupancy: two compositors steal each other's input
devices and the keyboard dies. To try the desktop without disturbing your
session, switch to a spare VT (`Ctrl+Alt+F3`), log in, and run:

```sh
rosella
```

That is `scripts/rosella-dev.sh`, installed to `~/.local/bin` by
`scripts/deploy.sh`. It stops any previous instance first — two compositors
on one seat steal each other's input devices and the keyboard dies — then
runs the freshly published build, logging to `/tmp/rosella.log`.

To try GPU-rendered Wayland clients on the VT build, run `ROSELLA_DMABUF=1 rosella`.
Rosella advertises DMA-BUF after verifying EGL import and readback on the
display GPU. It continues accepting shared-memory buffers. GPU client frames
are copied back to the CPU for compositing and scanout.

For a development session with live code updates, run `ROSELLA_DMABUF=1 rosella --live`
from the VT. This uses the .NET SDK and Python 3. Saving a supported C# edit
updates the running compositor and redraws the desktop. The watcher covers
the dock, Quick Settings, screenshot tool, renderer and their shared code.
If the .NET 10 watcher requires a restart for an edit, Rosella keeps running
and the watcher pauses live updates until the next launch. Build and reload
messages are in `/tmp/rosella.log`.

### Reload the taskbar in an installed session

Open the clock’s Quick Settings and choose **Modules → Reload** beside
**Taskbar & Quick Settings**. The submenu shows the active build and whether
the reload loaded a new build, reloaded the same build, or failed. Open
applications and keyboard focus stay in place. The clock’s right-click
menu also offers **Reload Taskbar**.

From the repository root, run `dotnet run --project tools/Rosella.Driver -- modules`
to compare the active and installed build IDs on the current desktop. Run
`dotnet run --project tools/Rosella.Driver -- reload taskbar` to reload the
module and print the result.

To publish a taskbar or Quick Settings code change, run
`scripts/deploy.sh --taskbar` and then reload the taskbar. This builds the
module against the published compositor. Updates to shared compositor code
still use a full publish and a new login.

### What you get

Rosella drives a connected monitor through Linux DRM/KMS. It uses the
saved GNOME primary output and mode when available, or the first usable
output at its preferred mode.

Independent desktops are currently limited to a Sony and Dell pair on
the same GPU. Mouse Forward selects the first desktop and Back selects
the second. Each keeps its own windows, pointer, and keyboard focus.

Displays blank after five minutes of inactivity unless an application
inhibits blanking. Supported BRAVIA displays can also use Simple IP Control
for standby and wake with `gnome-bravia-screensaver.config` in
`$XDG_CONFIG_HOME` (defaults to `~/.config`).

Super on its own is unbound; Alt+F4 closes a window, Alt+Tab switches windows
and Super+Esc quits.

### Customising pinned launchers

Drag an icon to reorder it within the pinned or running group. Right-click a
running app with a desktop entry and choose **Pin to taskbar**. Rosella saves
pin changes and the pinned order to the machine's launcher list.

Create `~/.config/rosella/launchers.json` (or `$XDG_CONFIG_HOME/rosella/launchers.json`
if `XDG_CONFIG_HOME` is absolute) with an ordered JSON array of desktop-file IDs:

```json
[
  "com.google.Chrome.desktop",
  ["org.mozilla.firefox.desktop", "firefox.desktop"],
  "org.gnome.Nautilus.desktop"
]
```

An inner array lists alternatives for one launcher. Rosella searches user and
system application directories in priority order, choosing the first existing
candidate within each directory; missing or unusable apps are skipped. IDs are
`.desktop` basenames, not commands or paths. Chrome retains its display and
profile handling. An empty array removes all pins. Without a file, Rosella uses
its built-in launchers; an invalid file is logged and also falls back to those
defaults. At most 32 launchers and 16 alternatives per launcher are accepted.
Pins that do not fit on the taskbar are not shown; keep the list short enough
for the display. Windows without a visible pin use their app icon or a
generic window icon on the taskbar.

For different settings on computers sharing a home directory, use
`launchers.<hostname>.json`, for example `launchers.workstation.json`. If present,
the hostname-specific file takes precedence over `launchers.json`; otherwise
the shared file is used. The hostname is the output of `hostname`. Changes take
effect when Rosella next starts; editing the file does not reload it live.

The ChatGPT launcher uses the installed application's normal profile and
connects to Wayland.

To start a pinned app automatically, right-click its dock icon and choose
**Start at Login**. Choose **Remove from Startup** to turn it off. Selections
are saved per machine and open once on the primary desktop after Rosella starts.

On two screens, right-click a dock icon on the destination screen and choose
**Move to This Screen**. Rosella moves the most recently raised matching window
from the other screen and gives it keyboard focus. Related dialogs follow it.

The pinned Chrome launcher uses XWayland when Rosella's bridge is available and
native Wayland otherwise. It checks profile ownership before each launch. Chrome
uses the regular profile on the primary screen when it is available. Flatpak
Chrome uses the persistent `rosella-user-data` profile on the second screen.
The profiles retain their own sign-ins and bookmarks. Rosella matches Flatpak's
sandbox-local lock PID to the running browser and checks that it owns the same
profile. Chrome recovers orphaned locks at startup. Rosella logs launches it
defers because an owner is still present or cannot be verified.
When the selected Chrome desktop entry uses an opaque wrapper, Rosella uses a
verified `com.google.Chrome.desktop` Flatpak command for execution if available,
while retaining the selected entry's name, icon and window matching. Wrapper
arguments and settings are not carried across. Explicit `--user-data-dir`
overrides are respected; if no suitable Flatpak entry exists, opaque wrappers
retain their own behaviour. Flatpak environment or filesystem overrides and
Chrome configurations hidden inside wrappers cannot be reliably inspected. The lock check is
best-effort and subject to races if Chrome starts or stops concurrently; it does
not remove locks, copy profiles or terminate processes.

Recovery if anything goes wrong: `Ctrl+Alt+F3`, log in,
`pkill -f Rosella.Compositor`, or simply pick GNOME at the next login
screen — it is always offered.

## Probe

```sh
dotnet run --project src/Rosella.Compositor -- --probe
```

Prints each card's driver, capabilities, connectors, encoders and modes, and
tests dumb-buffer allocation, and reports which output Rosella would select.
Connector queries use cached state without forcing a reprobe. Opening a card can acquire DRM master if no compositor owns it;
this diagnostic does not modeset or take input devices.

## Real-client check

Run the compositor and point any Wayland client at it (the compositor prints
its socket name — it takes the first free `wayland-N`, since another session's
compositor may already own `wayland-0`):

```sh
dotnet run --project src/Rosella.Compositor -- wayland-1
WAYLAND_DISPLAY=wayland-1 wayland-info
```

(`wayland-info` comes from the `wayland-utils` package.)

## Screenshots

Press **Print Screen** to freeze the display. Drag over the dimmed image
and release to capture that area from the original frame. Press **Esc** or
right-click to cancel. **Shift+Print Screen** captures the entire display.
The PNG is copied to the Wayland clipboard and saved in the Pictures folder's
`Screenshots` subdirectory, without the pointer or selection overlay.

## Audio

Open Quick Settings from the dock clock. **Audio output** shows the active
playback device. Select it to see the available devices and switch outputs.
The device picker uses PipeWire’s `pw-dump` and WirePlumber’s `wpctl`.
The hardware volume up, volume down and mute keys control the default
output. Volume steps by five percentage points.

## Frame timing

Rosella records frame timing automatically in `/tmp/rosella.log` when run
from a VT. Each sample covers 120 flips while the same app had keyboard
focus. It reports the wall-clock time, flip rate, render times, flip and
callback time, time between frames, and pixels repainted. A focus change
or a pause longer than a second starts a fresh sample. Flip rate measures
Rosella's output and includes time spent waiting for new content. Compare it
with the video's own frame rate and dropped-frame count.


## Smoke client

`tools/Rosella.SmokeClient` is a dependency-free Wayland client that draws an
animated window (gradient + moving bar) into an shm buffer, paced by frame
callbacks. Point it at a running Rosella to put a real client window on the
scanout:

```sh
dotnet run --project tools/Rosella.SmokeClient -- wayland-1
```

## Cursor theme

`tools/Rosella.CursorLab` writes the `Rosella-Ring-Glass` Xcursor theme.
The pointer has a translucent centre, a dark outline, a highlighted inner
rim and a grey dot at the hotspot. Resize and text cursors use their
conventional arrows and I-beams. Select `Rosella-Ring` to switch back to the
original ring theme if it is installed.

```sh
dotnet run --project tools/Rosella.CursorLab
dotnet run --project tools/Rosella.CursorLab -- /tmp/rosella-ring-glass
gsettings set org.gnome.desktop.interface cursor-theme Rosella-Ring-Glass
```

Previews of every shape at 48 px land in the theme's `previews/`. The
`check.c` harness asks the real libXcursor to load the generated files, so a
bad container or a shape aliased back to the ring fails without a desktop:

```sh
cc -O2 -o /tmp/cursor-check tools/Rosella.CursorLab/check.c -ldl
/tmp/cursor-check ~/.local/share/icons/Rosella-Ring-Glass
```


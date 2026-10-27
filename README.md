# Rosella

Working name — rename freely if something better comes along.

A heavily opinionated desktop environment, including its own compositor /
display server, written in C# on .NET 10. The point is to learn how desktop
environments and display servers actually work by building one, from the wire
protocol up.

Rosella is **not** a Mutter/GNOME Shell plugin and does not link libwayland.
It speaks the Wayland wire protocol itself in managed code, and talks to the
machine through the usual Linux graphics/input ABI seams:

- **DRM/KMS** (kernel) — modesetting, page flips, scanout
- **Mesa** (GBM/EGL, GL or Vulkan) — GPU buffers and rendering
- **libinput/evdev** — input devices

See `docs/architecture.md` for the layer map, the wire-format facts the codec
is built on, and the milestone plan.

## Design stance

Opinionated by design: almost no configurable settings. Defaults are chosen
and hardcoded; there is no settings surface to maintain. This is deliberate —
configuration is where desktop environments go to die.

## Status

- **M1 — Wayland wire codec** (`Rosella.Protocol`): done. Framing, signatures,
  all argument types (including untyped `new_id` expansion semantics), 24.8
  fixed-point, SCM_RIGHTS fd accounting, strict malformed-input rejection.
- **M2 — display server loop** (`Rosella.Transport`, `Rosella.Server`): done.
  Unix socket transport with SCM_RIGHTS, `wl_display` handshake, registry with
  globals, per-client object id spaces, `since`-version gating, stub interfaces
  that survive real clients (`wayland-info` exit test passed).
- **M3 — output** (`Rosella.Drm`): done, pending visual confirmation in the
  GDM session. DRM/KMS interop pinned against the kernel UAPI by golden tests,
  dumb buffers + mmap, framebuffers, legacy modeset, page-flip loop and a
  software-rendered test pattern on the first connected output. `--probe`
  introspects cards/connectors/modes and validates dumb buffers against the
  real driver.
- **M4 — client buffers**: next. `wl_shm`, `wl_surface`/`wl_compositor`/
  `wl_subcompositor`, compositing client surfaces into the scanout.

## Layout

```
src/Rosella.Protocol/          Wayland wire format + protocol description (embedded wayland.xml)
src/Rosella.Transport/         unix sockets, recvmsg/sendmsg, SCM_RIGHTS descriptor passing
src/Rosella.Server/            display server: sessions, object maps, dispatch, globals
src/Rosella.Drm/               DRM/KMS interop: devices, connectors, modes, dumb buffers, flips
src/Rosella.Compositor/        entry point: scanout loop, test pattern, --probe
scripts/                       GDM login-session installer and session wrapper
tests/Rosella.Protocol.Tests/  unit tests, including golden wire bytes
tests/Rosella.Server.Tests/    transport + end-to-end handshake tests
tests/Rosella.Drm.Tests/       kernel ABI golden layouts, ioctl codes, event parsing
tests/Rosella.Compositor.Tests/test pattern and flip matching
docs/architecture.md           design notes and roadmap
```

## Build & test

```sh
dotnet build
dotnet test tests/Rosella.Protocol.Tests/Rosella.Protocol.Tests.csproj
dotnet test tests/Rosella.Server.Tests/Rosella.Server.Tests.csproj
dotnet test tests/Rosella.Drm.Tests/Rosella.Drm.Tests.csproj
dotnet test tests/Rosella.Compositor.Tests/Rosella.Compositor.Tests.csproj
```

Note: `dotnet test` run against the `.slnx` at the repo root currently no-ops
(SDK 10.0.112) — invoke the test project directly as shown above.

Golden-byte tests assume a little-endian host (the wire format is native
byte order; Rosella targets x86_64/ARM64).

## Running as a login session

`scripts/install-session.sh` publishes Rosella and installs a GDM session
entry, so "Rosella" appears in the greeter's session chooser alongside GNOME.

Selecting it drives the first connected output at its preferred mode with the
milestone 3 test pattern (colour bars + a moving inverting column). Recovery
if anything goes wrong: `Ctrl+Alt+F3`, log in, `pkill -f Rosella.Compositor`,
or simply pick GNOME at the next greeter — it is always offered.

## Probe

```sh
dotnet run --project src/Rosella.Compositor -- --probe
```

Prints each card's driver, capabilities, connectors, encoders and modes, and
tests dumb-buffer allocation. Read-only apart from that allocation round trip
— safe to run inside a live session.

## Real-client check

Run the server and point any Wayland client at it:

```sh
dotnet run --project src/Rosella.Compositor -- wayland-1
WAYLAND_DISPLAY=wayland-1 wayland-info
```

(`wayland-info` comes from the `wayland-utils` package.)


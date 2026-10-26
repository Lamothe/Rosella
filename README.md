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
- **M2 — display server loop**: done. Unix socket transport with SCM_RIGHTS,
  `wl_display` handshake (`sync`/`get_registry`/`error`/`delete_id`), registry
  with globals, per-client object id spaces, `since`-version gating, stub
  interfaces (shm/seat/output/…) that survive real clients. Exit test passed:
  `wayland-info` (real libwayland) completes a full session against Rosella.
- **M3 — output**: next. DRM/KMS modesetting + dumb-buffer scanout.

## Layout

```
src/Rosella.Protocol/          Wayland wire format + protocol description (embedded wayland.xml)
src/Rosella.Transport/         unix sockets, recvmsg/sendmsg, SCM_RIGHTS descriptor passing
src/Rosella.Server/            display server: sessions, object maps, dispatch, globals
src/Rosella.Compositor/        the entry point (host process; compositor grows here)
tests/Rosella.Protocol.Tests/  unit tests, including golden wire bytes
tests/Rosella.Server.Tests/    transport + end-to-end handshake tests
docs/architecture.md           design notes and roadmap
```

## Build & test

```sh
dotnet build
dotnet test tests/Rosella.Protocol.Tests/Rosella.Protocol.Tests.csproj
dotnet test tests/Rosella.Server.Tests/Rosella.Server.Tests.csproj
```

Note: `dotnet test` run against the `.slnx` at the repo root currently no-ops
(SDK 10.0.112) — invoke the test project directly as shown above.

Golden-byte tests assume a little-endian host (the wire format is native
byte order; Rosella targets x86_64/ARM64).

## Real-client check

Run the server and point any Wayland client at it:

```sh
dotnet run --project src/Rosella.Compositor -- wayland-1
WAYLAND_DISPLAY=wayland-1 wayland-info
```

(`wayland-info` comes from the `wayland-utils` package.)


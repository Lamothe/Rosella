# GPU acceleration on Silver: what is actually possible

Date: 1 November 2026. Written against Rosella at `main` (M7), Silver running
Fedora 44, kernel 7.2.11-200.fc44.x86_64, Mesa 26.2.5.

This is a feasibility study, not a design. The question was narrow: with the
compositor burning 74% of a core at ~56 flips/s for one maximised window, is
there a GPU path on *this* machine that would actually be faster, and what
exactly is in the way?

Short answer: the Intel GPU renders fine, and a full 2880×1800 composite takes
2.3–6.1 ms of GPU time depending on the clock state. Every present path
available while simpledrm owns the panel adds 16–22 ms more, so the cheapest
complete GPU path measured is about 22 ms/frame against the 11 ms software
composite. The only path that could win — rendering straight into a buffer
simpledrm scans out — is accepted by `AddFB2`, but simpledrm still blits each
frame into the firmware framebuffer in the kernel, so zero-copy is impossible
and the remaining copy is unmeasured. Details and numbers below.

All numbers in this document come from `tools/Rosella.GpuProbe` unless they
are explicitly labelled otherwise. The 11 ms software baseline, the 74% CPU
figure and the 56 flips/s are the pre-existing profile from the task brief, not
re-measured here; they are consistent with each other (74% of a 17.9 ms frame
is 13.2 ms of CPU per frame, of which ~11 ms is compositing and the rest is
flip and loop overhead).

Everything was measured on Silver by the probe, over SSH, with the live Rosella
session present. The final run (quoted below) was on an idle machine — load
average 0.02 — so its numbers are not contended; earlier runs overlapped with
the compositor burning about three quarters of a core and produced 30–40%
higher `memcpy` times, which is why ranges are given rather than single values.
No kernel parameter, module, initramfs, package or git state was changed; the
compositor was not stopped, signalled or reconfigured; the probe never takes
DRM master, never modesets and never page-flips. (One probe build did segfault
in Mesa's `gbm_bo_unmap` during development; the kernel's coredump list records
only that probe process, and the compositor was untouched.)

## The machine, verified

| Item | Value |
|---|---|
| Model | MacBookPro11,3 (Retina 15-inch, late 2013) |
| Kernel | 7.2.11-200.fc44.x86_64, Fedora 44 |
| Mesa | 26.2.5 (EGL 1.5, GBM 1.0) |
| 00:02.0 | Intel Crystal Well / Iris Pro 5200 [8086:0d26], `i915` bound, no connectors |
| 01:00.0 | NVIDIA GK107M GT 750M Mac Edition [10de:0fe9], no driver bound, no DRM node |
| card0 | `simple-framebuffer` (simpledrm) over the EFI framebuffer; card0-Unknown-1 connected, 2880×1800, the only connected connector on the machine |
| card1 | `i915`, zero connectors |
| renderD128 | `i915` render node; GBM and EGL work here |
| Kernel cmdline | `... rd.driver.blacklist=nouveau modprobe.blacklist=nouveau mem_sleep_default=s2idle` |
| Installed NVIDIA userspace | 615.78.08 (does not support Kepler) |

The i915 connector loss is real and reproduced in this boot's kernel log:

```
i915 0000:00:02.0: [drm] [ENCODER:92:DDI A/PHY A] failed to retrieve link info, disabling eDP
i915 0000:00:02.0: [drm] Cannot find any crtc or sizes
```

`apple_gmux` is loaded, the panel backlight is driven by i915 (`PCH PWM1
enabled`), but i915 exposes no connector, so the live session scans out through
simpledrm dumb buffers.

Separate matter, noted and not chased: every i915 runtime-suspend logs a
warning at `assert_can_disable_lcpll` / `hsw_disable_lcpll` from
`i915_pm_runtime_suspend`. It does not stop the device resuming or rendering,
but the GPU power state churns whenever it goes idle, and it is why dmesg on
Silver is full of WARNING stacks.

## 1. Is the Intel GPU usable for offscreen rendering today?

**Verdict: usable. No kernel or driver change needed, and no package installs.**

Measured on `/dev/dri/renderD128` (GBM → EGL platform display → surfaceless
context):

| Capability | Result |
|---|---|
| GBM device | created on renderD128, backend `drm` |
| EGL | 1.5, vendor "Mesa Project", client APIs `OpenGL OpenGL_ES` |
| EGL extensions | `EXT_image_dma_buf_import`, `..._modifiers`, `KHR_surfaceless_context`, `KHR_create_context` all present |
| GLES (2.0 requested) | `OpenGL ES 3.2 Mesa 26.2.5`, GLSL ES 3.20 |
| GLES (3.x requested) | `OpenGL ES 3.2 Mesa 26.2.5`, GLSL ES 3.20 |
| Desktop GL | `4.6 (Compatibility Profile) Mesa 26.2.5`, GLSL 4.60 |
| Renderer | `Mesa Intel(R) Iris(R) Pro Graphics P5200 (HSW GT3)` |
| Limits | `GL_MAX_TEXTURE_SIZE=16384`, `GL_MAX_VIEWPORT_DIMS=16384×16384` (2880×1800 fits easily) |
| Vulkan (default loader) | hasvk device `Intel(R) Iris(R) Pro Graphics P5200 (HSW GT3)`, api 1.2.354, plus llvmpipe |
| Vulkan, `VK_DRIVER_FILES=/usr/share/vulkan/icd.d/intel_icd.x86_64.json` (anv) | `vkEnumeratePhysicalDevices` fails with VkResult −3 (`VK_ERROR_INITIALIZATION_FAILED`) — anv has no Haswell support |
| Vulkan, `VK_DRIVER_FILES=/usr/share/vulkan/icd.d/intel_hasvk_icd.x86_64.json` | enumerates the Iris Pro; Mesa prints `warning: Haswell Vulkan support is incomplete` |

So: **GL/GLES is fully usable; Vulkan exists only through `intel_hasvk`, and
Mesa itself labels that support incomplete.** A compositor built on Vulkan here
would be standing on a driver Mesa describes as incomplete. GLES 3.2 through
iris is the sane target.

Mesa hands back a 3.2 context even when ES 2.0 is requested, which is why both
GLES rows read the same. The probe therefore tries `#version 300 es` shaders
first and keeps a 1.00 pair as a fallback; on this driver the 1.00 pair failed
to compile (with an empty log), so the 3.00 path is the one in use.

The probe also proves the rendering is real: after a frame, the readback has
197 distinct colours sampled across the image, the centre pixel is
`0xFF22242A` (bytes B=0x22, G=0x24, R=0x2A), the corner `0xFF0B0C2E`,
`glGetError` is 0, and the copy into the dumb buffer is byte-for-byte identical
to the readback. The probe reads back as `GL_BGRA` deliberately: the scanout
format is `DRM_FORMAT_XRGB8888`, whose little-endian byte order is B,G,R,X, so
`GL_RGBA` would have produced a colour-swapped frame (timings are unaffected;
the colour order is not).

One performance caveat belongs to this verdict: Haswell RPS does not sit at its
maximum. Idle is 200 MHz (RPn), maximum 1200 MHz (RP0). The probe samples the
frequency every frame; across runs the median during the render-only loop was
anywhere from 350 to 1200 MHz, and sustained load has been seen settling at
550–600 MHz. This is the single largest source of variance in the numbers
below.

## 2. Cheapest present path while the panel is simpledrm

**Verdict: usable-with-caveats for rendering, unusable as a performance win.
Every complete present path measured is slower than the 11 ms software
composite.**

The probe renders four draw calls at 2880×1800 — tiled wallpaper, one opaque
window with a title bar, and a blended 32×32 cursor, about 8.8 M fragments —
then measures (a) the render, (b) the transfer to the scanout-shaped dumb
buffer, and (c) any extra CPU copy. Medians over 60 frames after a 2 s GPU-clock
warm-up, final run, live compositor present but idle, load average 0.02:

| Path | Render | Transfer | Copy | Total | vs 11.0 ms |
|---|---|---|---|---|---|
| GPU composite only (never presents) | 6.06 ms | — | — | 6.06 ms | 0.55× |
| GPU → `glReadPixels` → dumb | 9.96 ms | 18.39 ms | — | 28.20 ms | 2.56× |
| GPU → `glReadPixels` into a PBO → `memcpy` → dumb | 10.00 ms | 6.98 ms | 5.29 ms | **22.28 ms** | **2.03×** |
| GPU → readback → linear → `memcpy` → dumb | 10.03 ms | 18.42 ms | 7.51 ms | 36.09 ms | 3.28× |
| GPU → linear GBM bo → `memcpy` → dumb | 33.59 ms | — | 6.64 ms | 39.41 ms | 3.58× |
| `memcpy` only, linear → dumb | — | — | 2.27 ms | 2.27 ms | 0.21× |
| `memcpy` only, linear → linear | — | — | 1.78 ms | 1.78 ms | 0.16× |

Render times and clock states across the runs are worth reporting honestly,
because they vary a lot:

| Run | Render-only | GPU clock as printed | Readback (direct) |
|---|---|---|---|
| 09:58 | 2.33 ms | 900–1200 (min–max) | 9.30 ms |
| 10:24 | 3.52 ms | 600 median | 18.48 ms |
| 10:34 | 6.06 ms | 500 median | 18.39 ms |

The render cost tracks the clock, not the frame: the same scene takes 2.3 ms at
900–1200 MHz and 6 ms at 500 MHz. In every present variant the median clock fell to
200–500 MHz, because the CPU-side transfer leaves the GPU idle between frames
and i915's RPS decays quickly. That is a real property of this path, not a
benchmark artefact: a compositor that stalls its GPU every frame will run its
GPU at low clocks.

The cheapest complete path is the PBO readback at **22.3 ms median, p95
22.7 ms**, 44.9 fps equivalent — against 11 ms for the software composite. In
an earlier, faster-clock run the direct readback into the dumb buffer came in
at 19.3 ms, so the ordering among present variants is noisy, but no run has
produced a complete GPU path under 19 ms. The PBO route is meaningfully better
than the direct `glReadPixels` (6.98 ms transfer versus 18.39 ms), which is
itself a useful finding: on iris, `glReadPixels` straight into client memory
is pathological, and a pixel-pack buffer is the right mechanism.

Why the present path is so expensive: a 2880×1800 frame is 20,736,000 bytes.

| Transfer | Measured | Throughput |
|---|---|---|
| `glReadPixels` direct into the destination | 9.3–18.5 ms | 1.1–2.2 GB/s |
| `glReadPixels` into a PBO + map | 6.98 ms | 3.0 GB/s |
| Copy out of a mapped PBO into the dumb buffer | 5.29 ms | 3.9 GB/s |
| Copy out of an i915 GEM/dma-buf mapping (read GEM + write dumb) | 6.1–7.5 ms | 2.8–3.4 GB/s |
| Cached `memcpy` linear → dumb | 1.8–2.5 ms | 8.3–11.6 GB/s |
| Cached `memcpy` linear → linear | 1.7–1.8 ms | 11.6–12.1 GB/s |

**Where the copy bandwidth tops out:** about 11.6–12.5 GB/s for a cached copy
on this DDR3L machine (the theoretical figure for dual-channel DDR3L-1600 is
25.6 GB/s, so ~47% efficiency is normal for a read+write copy). A full frame
therefore cannot be copied in less than ~1.7 ms, and any GPU-mediated transfer
measured here is 3–10× worse. Since the software compositor already spends
11 ms touching the frame, saving 8 ms of blit and then paying 7–18 ms for a
readback is a net loss.

Copy cost by damage size (linear → dumb, row by row):

| Rect | Bytes | Median | Throughput |
|---|---|---|---|
| 64×64 (cursor) | 16,384 | 0.001 ms | 15–28 GB/s |
| 2400×1000 (window) | 9,600,000 | 0.76–1.02 ms | 9.4–12.6 GB/s |
| 2880×1800 (full) | 20,736,000 | 1.78–2.01 ms | 10.3–11.7 GB/s |

That table is the good news for a GPU design: if the compositor ever has a
GPU-rendered frame in ordinary cached memory, damage-sized copies into the dumb
buffer are cheap. The problem is getting the frame *out* of the GPU at
cached-memory speeds. (The full-frame row in the timing table reads 2.27 ms
against 1.78 ms here for the same loop. They are measured at different points
in the run — the damage table runs once the GPU has gone idle — so the gap is
run-to-run variance, not a different mechanism.)

## 3. Is zero-copy scanout from an i915 buffer possible on simpledrm?

**Verdict: no, zero-copy is impossible. The import itself works, which is the
surprising part, but simpledrm always performs one kernel blit per frame.**

Measured, with no master taken and no flip issued. `AddFB2` with a dma-buf fd
passed raw as a handle fails, which is an API artefact, not simpledrm's
limitation: a dma-buf fd is not a GEM handle in that DRM file, so
`drmModeAddFB2` returns `ENOENT (2)`. The correct sequence is
`drmPrimeFDToHandle` first, and then it works:

| Target | Source | Call | Result |
|---|---|---|---|
| renderD128 | GBM bo, 2880×1800 XRGB8888, linear, pitch 11520, modifier 0 (LINEAR) | `gbm_bo_create` | OK, dma-buf fd 50 |
| card0 | dumb handle 2 | `drmModeCreateDumbBuffer` | OK, pitch 11520, 20,738,048 bytes |
| card0 | dumb handle 2 | `drmModeAddFB2` | **OK** (control: the call works) |
| card0 | dma-buf fd 50 raw | `drmModeAddFB2` | **ENOENT (2)** |
| card0 | dma-buf fd 50 raw | `drmModeAddFB2WithModifiers` | **ENOENT (2)** |
| card0 | dma-buf fd 50 | `drmPrimeFDToHandle` | **OK**, handle 3 |
| card0 | imported handle 3 | `drmModeAddFB2` | **OK** |
| card1 | dma-buf fd 50 | `drmPrimeFDToHandle` | OK |
| card1 | imported handle 1 | `drmModeAddFB2` | OK |

The raw-fd row is only meaningful if the fd number cannot collide with a GEM
handle in the same DRM file; the probe checks that (`fd 50` against handles
1–3), prints the result, and skips the row rather than guess if they overlap.

So simpledrm's PRIME import works, and it will build a framebuffer over an
i915-rendered buffer today. Two caveats, both material:

1. **The flip is untested.** `AddFB2` proves the framebuffer object exists; it
   does not prove the primary plane's atomic check accepts it at commit time,
   nor that the flip completes. Testing that requires DRM master and a page
   flip, which this investigation was explicitly not allowed to do.
2. **simpledrm is not zero-copy even when it accepts the buffer.** Its
   primary-plane `atomic_update` blits the framebuffer into
   `sdev->screen_base`, the firmware framebuffer, using `drm_fb_blit()`
   (`drivers/gpu/drm/tiny/simpledrm.c`; the modern form walks damage clips when
   the compositor attaches them). Rosella's legacy `drmModePageFlip` supplies
   no damage clips, so every flip would blit the full 20.7 MB in the kernel.
   One copy, done by the kernel, instead of a userspace copy — not zero copies.
   The cost of that blit is **not measured** and depends on how the kernel maps
   the imported object; the copy table above brackets it between ~1.7 ms
   (cached) and ~7 ms (write-combined read).

The same mechanism explains a probe wrinkle worth recording: the probe can
render straight into a linear GBM bo through an EGLImage, but Mesa's
`gbm_bo_unmap` of that imported render target segfaults (on i915 *and*
amdgpu), and a plain `mmap` of the dma-buf is write-combined, so reading a
rendered frame back on the CPU costs 6–7 ms. A Rosella implementation must
never read back such a buffer; it has to present it.

## 4. Can i915 be made to own the panel?

**Verdict: unusable today. There is no supported workaround, and the only
paths with any chance are out-of-tree kernel work.**

What the message actually is: `intel_dp_get_dpcd()` failed during eDP
connector init, so i915 treats DDI A as a "ghost" eDP port and removes the
connector. It is not an EDID problem — EDID is read after link info, and over
the same AUX channel — and it is not primarily a VBT problem: the VBT declares
the port, but the connector dies because the panel's DPCD could not be read.

Why on this machine: Retina MacBook Pros switch the internal panel with a
separate eDP mux chip controlled by gmux, and that chip **cannot switch the AUX
channel separately from the main link**; the in-tree `apple-gmux` driver
documents this limitation for Retina models (pre-Retina Macs can switch DDC
separately, which is why this failure is a Retina-era thing). The panel's DPCD
has `NO_AUX_HANDSHAKE_LINK_TRAINING`, so the inactive GPU can use
pre-calibrated link parameters, but i915 still cannot read DPCD to learn them.
At boot, whichever GPU the firmware and gmux left in charge owns AUX; i915 is
not it, so it sees a ghost port.

What has been tried upstream:

- **Reprobe eDP/LVDS on hotplug** (Lukas Wunner, 2015): keeps the connector
  registered and retries. The series lists `MBP 11,3 2013 intel HSW + nvidia
  GK107 retina` as "work in progress". Not merged for this model.
- **Proxy DPCD/EDID through the active GPU** (same series): marked
  EXPERIMENTAL and eDP-only; on this machine i915 rejected the proxied mode
  with `CLOCK_HIGH` and nouveau aborted link training halfway. Never merged.
- **gmux display muxing for Retina Macs** (2016 series): pre-Retina machines
  were the tested configuration; Retina support was experimental and not
  merged.

Workarounds people ask about, and why they do not apply:

| Idea | Effect here |
|---|---|
| `edid_firmware=edp-1:...` | No. The connector is destroyed before EDID is read; the failure is the DPCD read. |
| `i915.vbt_firmware=...` | A real module option, and could correct VBT-derived link configuration, but it does not make the DPCD read succeed. No VBT for this panel exists. |
| `i915.edp_vswing=2`, `panel_use_ssc`, `lvds_channel_mode` | Link-training and LVDS parameters. The link never gets that far. |
| `video=...` / forcing a connector | There is no connector to force, and no i915 parameter creates one. |
| Experimental AUX-proxy / reprobe patches | The only approach with a plausible mechanism. Requires a custom kernel, was last touched in 2016, and is marked WIP for exactly this model. High risk of a black panel or an unstable GPU. |

Risk summary: every option requires a reboot, which this investigation was not
allowed to perform, and none is supported. The experimental kernel route would
be days of work with a real chance of leaving the machine without a display,
for a payoff that the present-path measurements have already made doubtful.

## 5. Is the GT 750M worth anything?

**Verdict: unusable, and worth nothing for this problem.**

First, the record: open-source support for this GPU *does* exist. Nouveau's
kernel driver claims every NVIDIA display-class PCI device including 0fe9,
Silver has Mesa's `nouveau_dri.so` (nvc0 GL), and since Mesa 25.2 NVK covers
Kepler at Vulkan 1.2 — so the objection is not the absence of a driver. It is
everything else below.

Four independent reasons, each sufficient. (1) The installed driver is
615.78.08, and Kepler support ended with the 470 branch; 615 cannot bind this
GPU at all. (2) The only proprietary driver that supports GK107 is 470.256.02,
which is EOL (September 2024) and, decisively for Rosella, has **no GBM and no
`EGL_PLATFORM_GBM`** — GBM arrived with 495 — so a GBM/EGL compositor cannot
use it even if it built. (3) It will not build cleanly against a 7.2 kernel
without RPM Fusion's ongoing patches; the F44 package exists and RPM Fusion has
been chasing kernel API changes through 6.12–7.0.x (researched, not verified on
this machine, and the running kernel is newer than the patched builds found),
and `nouveau` is blacklisted on this machine's cmdline anyway. (4) Even if all
of that were solved, this is a Retina Mac: the internal panel is driven by the
iGPU through gmux, the dGPU has no connectors of its own, and nouveau's Kepler
support is manual pstate selection only — engine and memory reclocking WIP,
automatic reclocking TODO — with the Retina gmux muxing patches never merged.
The best case is a render-only device behind another cross-GPU copy, i.e.
strictly worse than the iGPU path measured above, for more power and heat.

## Recommendations

**Do not build a GPU present path on the current board state.** The measured
cheapest complete path is 22.3 ms/frame against 11 ms today. That is not a
matter of tuning; it is the cost of moving 20.7 MB out of the GPU.

**The one experiment worth doing next** is the one this investigation could
not: prove or disprove the zero-copy-ish flip. It needs DRM master and one page
flip of an imported i915 framebuffer on card0, on a spare VT or with Rosella
stopped by you (not by me). Measure flip completion latency (the flip event
timestamp) and check dmesg for atomic-check failures. If simpledrm accepts the
flip and completes it in ~2–7 ms, the GPU path becomes attractive: the
compositor's CPU cost collapses, the 74% core goes away, and the frame budget
is set by a 2.3–6 ms GPU render plus simpledrm's kernel blit. If it rejects the
framebuffer or the blit is 10+ ms, the answer is to stay in software.

Rough cost if that experiment succeeds — this is a real renderer, not a tweak:

| Piece | Estimate |
|---|---|
| `src/Rosella.Gpu`: EGL/GBM/GLES P/Invoke, context and capability probing (the probe is most of the interop) | 4–5 days |
| Scene → GPU: wallpaper, window quads, scale, alpha, damage scissor, cursor | 4–5 days |
| Client `wl_shm` → GL textures, upload on commit, release/frame pacing, buffer-age equivalent | 4–5 days |
| Present: GBM bo, dma-buf, `PRIME_FD_TO_HANDLE`, `AddFB2`, flip, fallback to the software path | 3–5 days |
| Tests, VM/virtio-gpu fallback, GDM session checks, regression pass | 3–4 days |
| **Total** | **~3–4 weeks**, with a hard dependency on the flip experiment |

The fallback matters: the software path must stay, because a VM with
virtio-gpu, or any machine where the GPU cannot present, still has to work.
That also means the GPU path can land behind a capability check rather than a
setting (Rosella has no settings surface).

**The cheaper alternative, if the flip experiment fails:** attack the software
blitter. 60% of samples are in the per-pixel loop and 11% in `memcpy`; the
measured cached-copy floor for a frame is 1.7 ms, so the blit's ~9 ms of
per-pixel work is the target. Vectorising the opaque 1:1 path and the
integer-scale expansion, and turning ARGB rows that are fully opaque into
`memcpy`, is days of work rather than weeks and does not depend on any driver
behaviour. It will not reach GPU numbers, but it is the best return available
while simpledrm owns the panel.

## What could not be determined, and why

- **Whether simpledrm accepts an imported i915 framebuffer at flip time.**
  Needs DRM master and a page flip, both prohibited here. `AddFB2` succeeding
  is necessary but not sufficient.
- **What simpledrm's `drm_fb_blit` costs per flip on this machine.** Needs a
  flip to time. The copy table brackets it between ~1.7 ms (cached source) and
  ~7 ms (write-combined source), but which mapping the kernel uses is not
  known.
- **Whether an asynchronous readback with a fence would beat the PBO path.**
  The probe maps the PBO immediately, so the map includes the wait; a fence and
  a later map might overlap the transfer with the next frame's render. It still
  moves 20.7 MB through the same bus, so the ceiling is the ~12 GB/s memcpy
  figure, not the 3 GB/s measured.
- **Whether i915 can ever own this panel.** No supported route exists; the only
  mechanisms are unmerged 2015–2016 patch series. Research only, as instructed.
- **Compositor-side numbers under a fixed GPU clock.** i915's RPS moves the
  render between 2.3 ms and 6.1 ms for identical work, and nothing in userspace
  can pin it. The final run was on an idle machine, so CPU contention is not
  the explanation; the clock is. Medians and the ordering of present paths are
  stable across runs, individual milliseconds are not.

## The probe

`tools/Rosella.GpuProbe` is a standalone console app (no package references,
no project references) that does the measurements above. It is deliberately
self-contained so it can run on a machine with no Rosella build and no
`dotnet` runtime installed: publish it self-contained on Mercury and copy the
directory over.

```sh
# on Mercury
dotnet publish tools/Rosella.GpuProbe/Rosella.GpuProbe.csproj \
  -c Release -r linux-x64 --self-contained true -o /tmp/gpuprobe
rsync -a /tmp/gpuprobe/ silver:/tmp/gpuprobe/

# on Silver, over SSH
/tmp/gpuprobe/Rosella.GpuProbe                  # full run, ~60 s
/tmp/gpuprobe/Rosella.GpuProbe --frames 20      # quicker, noisier
/tmp/gpuprobe/Rosella.GpuProbe --vulkan-only    # ICD enumeration only
/tmp/gpuprobe/Rosella.GpuProbe --skip-linear-target
```

What it does, in order: enumerates the DRM nodes, connectors and modes from
sysfs; creates a GBM device and surfaceless EGL context on the render node;
reports the EGL/GLES/desktop-GL/Vulkan capability set; creates a
scanout-shaped dumb buffer on card0; measures the GPU composite and the present
paths into it; measures the dma-buf → `AddFB2` sequence on card0 with a
dumb-buffer control; and prints everything as tables. Options: `--render-node`,
`--card0`, `--card1`, `--frames`, `--warmup`, `--skip-linear-target`,
`--vulkan-only`, `--help`.

Deliberate limits, enforced by construction: no `drmSetMaster`, no
`drmModePageFlip`, no `drmModeSetCrtc`, no module load/unload, no sysfs writes,
no package installation. It opens card nodes read-write only for dumb buffers,
`PRIME_FD_TO_HANDLE` and `AddFB2`, none of which disturb a live DRM master.
Vulkan is reporting only and its failure is non-fatal.

Two implementation notes for anyone extending it. First, both `gbm_bo_unmap`
and a plain dma-buf `mmap` are write-combined on i915, so CPU reads of a
rendered GEM buffer run at ~3 GB/s; and Mesa's `gbm_bo_unmap` of a bo imported
as an EGLImage render target segfaults on Mesa 26.2.5 (i915 and amdgpu), which
is why the probe maps the dma-buf instead. Second, after the capability probe
(which ends with `eglMakeCurrent(EGL_NO_CONTEXT)`), the probe rebinds its ES
context explicitly rather than assuming it survived; in one earlier ordering
the context was gone by the time the first GL object was created.

## Appendix: probe output, final run (1/11/2026, 10:34, live compositor running)

Excerpt, but every block the body cites is included.

```
Rosella GPU probe
  when       : 2026-11-01 10:34:17 (local)
  kernel     : 7.2.11-200.fc44.x86_64
  loadavg    : 0.02 0.05 0.35 1/687 25315
  runtime    : .NET 10.0.12

== Devices ==
  /dev/dri/card0: driver=simple-framebuffer
    card0-Unknown-1              connected    enabled=enabled  modes=1 first=2880x1800
  /dev/dri/card1: driver=i915
  /dev/dri/renderD128: driver=i915
  note: this probe never takes DRM master, never page-flips and never modesets.

egl: EGL 1.5, 1.5
== EGL / GBM ==
  render node   : /dev/dri/renderD128 (fd 40, kernel driver i915)
  GBM backend   : drm
  EGL vendor    : Mesa Project
  EGL version   : 1.5
  EGL APIs      : OpenGL OpenGL_ES
  EGL dma-buf   : EXT_image_dma_buf_import=True modifiers=True surfaceless=True create_context=True

== API generations (as enumerated by Mesa on this device) ==
  GLES (ES2 requested) : OpenGL ES 3.2 Mesa 26.2.5 | Mesa Intel(R) Iris(R) Pro Graphics P5200 (HSW GT3)
                  vendor: Intel | GLSL: OpenGL ES GLSL ES 3.20
  GLES (ES3 requested) : OpenGL ES 3.2 Mesa 26.2.5 | Mesa Intel(R) Iris(R) Pro Graphics P5200 (HSW GT3) | GLSL: OpenGL ES GLSL ES 3.20
  Desktop GL    : 4.6 (Compatibility Profile) Mesa 26.2.5 | Mesa Intel(R) Iris(R) Pro Graphics P5200 (HSW GT3) | GLSL: 4.60
  context       : rebind ES api=1, makeCurrent=1, eglError=EGL_SUCCESS
  limits        : GL_MAX_TEXTURE_SIZE=16384, GL_MAX_VIEWPORT_DIMS=16384x16384
  readback fmt  : GL_EXT_read_format_bgra=True GL_EXT_texture_format_BGRA8888=True

== Vulkan ==
  VK_DRIVER_FILES  = (unset)
  VK_ICD_FILENAMES = (unset)
  ICD intel_hasvk_icd.x86_64.json
  ICD intel_icd.x86_64.json
  ...
MESA-INTEL: warning: Haswell Vulkan support is incomplete
  2 physical device(s):
    [0] Intel(R) Iris(R) Pro Graphics P5200 (HSW GT3) | type=IntegratedGpu | api=1.2.354 | driver=0x06802005 | vendor=0x8086 device=0x0D26
    [1] llvmpipe (LLVM 22.1.8, 256 bits) | type=Cpu | api=1.4.354 | driver=0x06802005 | vendor=0x10005 device=0x0000

== Scanout-shaped dumb buffer (simpledrm card0) ==
  2880x1800 | pitch 11520 bytes | 20738048 bytes (19.8 MiB) | GEM handle 1
  frame bytes 20736000
  GL context   : makeCurrent=1, OpenGL ES 3.2 Mesa 26.2.5, eglError=EGL_SUCCESS

== Timing: 2880x1800, median over 60 frames after warm-up ==
  variant                                        render ms  readback ms   copy ms  total ms   fps   vs 11.0 ms
  gpu composite only                                6.06        0.00      0.00      6.06  165.0     0.55x
                                               p95 total 9.80 ms | min 3.71 | max 9.82 | 3.4 GB/s | GPU clock median 500 MHz (200-900)
  gpu -> glReadPixels -> dumb                       9.96       18.39      0.00     28.20   35.5     2.56x
                                               p95 total 28.65 ms | min 17.60 | max 28.70 | 0.7 GB/s | GPU clock median 200 MHz (200-500)
  gpu -> glReadPixels(PBO) -> memcpy -> dumb       10.00        6.98      5.29     22.28   44.9     2.03x
                                               p95 total 22.73 ms | min 22.07 | max 27.77 | 0.9 GB/s | GPU clock median 200 MHz (200-500)
  gpu -> readback -> linear -> memcpy -> dumb      10.03       18.42      7.51     36.09   27.7     3.28x
                                               p95 total 37.31 ms | min 26.39 | max 41.91 | 0.6 GB/s | GPU clock median 200 MHz (200-650)
  render sanity : centre=0xFF22242A corner=0xFF0B0C2E glGetError=0 viewport=2880x1800
  readback sanity: 197 distinct sampled colours (>1 means the GPU drew something)
  dumb buffer matches the readback: True
  memcpy only (linear -> dumb)                      0.00        0.00      2.27      2.27  440.1     0.21x
                                               p95 total 5.45 ms | min 1.78 | max 6.57 | 9.1 GB/s | GPU clock n/a
  memcpy only (linear -> linear)                    0.00        0.00      1.78      1.78  560.7     0.16x
                                               p95 total 1.81 ms | min 1.77 | max 1.84 | 11.6 GB/s | GPU clock n/a

== Copy cost by damage size (linear -> dumb, row by row) ==
  damage rect          bytes        median ms   GB/s (payload)
    64x64         16,384        0.001      28.22
  2400x1000    9,600,000        0.764      12.57
  2880x1800   20,736,000        1.780      11.65

== GPU-rendered linear buffer (EGLImage over a GBM bo) ==
  bo: 2880x1800 XRGB8888 pitch=11520 offset=0 modifier=0x0000000000000000 dma-buf fd 50
  render target imported: 2880x1800, pitch 11520
  gpu -> linear gbm bo -> memcpy -> dumb           33.59        0.00      6.64     39.41   25.4     3.58x
                                               p95 total 41.84 ms | min 29.71 | max 46.10 | 0.5 GB/s | GPU clock median 200 MHz (200-1200)

== dma-buf import on the simpledrm card node (no flip, no master) ==
  target            source                              call                        result
  renderD128        gbm_bo_create                       2880x1800 XRGB8888 pitch=11520 offset=0 modifier=0x0000000000000000 OK, dma-buf fd 50
  card0             dumb handle 2                       drmModeCreateDumbBuffer     OK, pitch=11520, 20738048 bytes
  card0             dumb handle 2                       drmModeAddFB2               OK
  card0             dma-buf fd 50 raw                   drmModeAddFB2               ENOENT (2)
  card0             dma-buf fd 50 raw                   drmModeAddFB2WithModifiers  ENOENT (2)
  card0             dma-buf fd 50                       drmPrimeFDToHandle          OK
  card0             imported handle 3                   drmModeAddFB2               OK
  card1             dma-buf fd 50                       drmPrimeFDToHandle          OK
  card1             imported handle 1                   drmModeAddFB2               OK
```
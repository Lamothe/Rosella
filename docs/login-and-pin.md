# Login, PIN and the greeter

Date: 1 November 2026. Applied: greetd 0.10.3 replaced GDM as the display
manager, and Rosella's own greeter draws the login screen at the compositor's
scale, logging in with the PIN or the password (see "Applied on Silver" at the
end). The M8 lock screen is still a plan.

Goal: boot straight into Rosella, log in with a PIN instead of a password, and
have the screen lock accept the same PIN. Replace GDM, because it is the part
that cannot be taught to do either of those things.

## What GDM actually provides here, and what loses out

Checked on Silver, not assumed:

| GDM feature | Status | Replaced by |
|---|---|---|
| Session picker | used (three entries) | greetd reads the same `/usr/share/wayland-sessions/*.desktop` |
| X11 sessions | `/usr/share/xsessions` is empty | nothing to lose |
| Fast user switching | one account | not used |
| Fingerprint / smartcard | no reader, no smartcard | not used |
| XDMCP | `#Enable=true`, commented out | not used |
| Autologin | not used | greetd `[initial_session]` |
| GNOME lock screen | only if GNOME is chosen at the greeter | Rosella's own lock screen (M8) |
| Login-screen a11y | not needed | — |
| HiDPI login screen | **broken**: GDM renders at 100%, the password field is ~8 px tall on a 2880×1800 panel | Rosella draws at `ScalePolicy` scale (below) |

Only `gnome-initial-setup` requires the `gdm` package, so GDM can stay
installed and merely disabled: `systemctl disable gdm` is the whole removal,
and `systemctl start gdm` on another VT is the way back.

## Scaling: reuse `ScalePolicy`, do not reimplement it

The compositor already computes the right scale, in one place:

- `src/Rosella.Compositor/ScalePolicy.cs` — mutter's `calculate_scale`
  (135 DPI target below 20 inches, 110 above, integer 1–4), plus the
  resolution fallback for outputs that report no physical size or the exact
  96-DPI placeholder simpledrm fabricates.
- `src/Rosella.Compositor/Program.cs:63` — the only call site:
  `ScalePolicy.For(mode.HDisplay, mode.VDisplay, mmWidth, mmHeight)`, feeding
  `scene.Scale`, `shell.Pinned`, and `wl_output`'s scale.
- `tests/Rosella.Compositor.Tests/FieldReportTests.cs:22-47` — the formula and
  the fabricated-size fallback are both pinned by tests.

On Silver it resolves to 2, confirmed from the live session's own log:

```
Nov 01 04:19:32 silver gdm-wayland-session[2941]: rosella: output scale 2 (1440x900 logical)
```

(`/sys/class/drm/card0-Unknown-1/edid` is empty on this machine, so the
connector reports no physical size; `ForUnknownSize` gives 2 for a 2880×1800
panel, which is also what the fabricated-size test asserts.)

Therefore the greeter is **Rosella itself**, drawing through the same `Scene`,
at the same scale, with the same fonts. A login screen whose text is 2× and
whose hit targets are 2× falls out of that with no extra work, and stays
correct on Mercury's 34-inch 1× panel or a 4K TV without a setting.

Rejected, with reasons:

- **tuigreet** — draws in the text console; `FONT=eurlatgr` at 2880×1800 gives
  360×112 cells, i.e. the same unreadable text, and a larger console font is a
  second, worse scale policy that only works on this one machine.
- **`/etc/xdg/monitors.xml`** — makes *gnome-shell*'s greeter scale. It does
  nothing for Rosella, and it is exactly the "half-assed implementation"
  problem: a per-machine workaround for a value the compositor already knows.
- **Fractional scaling** — `ScalePolicy` is deliberately integer-only; the
  greeter inherits that decision rather than inventing a second one.

## Architecture

```
greetd (system service, owns VT 1)
  └── greeter: /usr/local/bin/rosella --greeter      (user: greeter; video,input)
        ↕ GREETD_SOCK, length-prefixed JSON
        └── on success: start_session
              └── /usr/local/bin/rosella-session      (user: michael)
                    └── Rosella.Compositor
```

greetd 0.10.3 and its config are packaged for F44 (`greetd`, 1.3 MiB), and the
package ships `/etc/greetd/config.toml`, `/etc/pam.d/greetd`,
`/etc/pam.d/greetd-greeter` and `greetd.service`. No greeter package is needed,
because the greeter is Rosella.

`/etc/greetd/config.toml`:

```toml
[terminal]
vt = 1

[default_session]
command = "/usr/local/bin/rosella --greeter"
user = "greeter"
```

`greeter` must exist and be in `video` and `input` (DRM node and evdev), and
must not be in `michael`'s groups: `useradd --system --no-create-home -G
video,input greeter`.

### The IPC detail that bites

greetd's protocol is **not** line-delimited. Each message is a 4-byte
little-endian length followed by that many bytes of UTF-8 JSON
(`greetd-ipc(7)`). Sequence for a PIN login:

```
→ {"type":"create_session","username":"michael"}
← {"type":"auth_message","auth_message_type":"secret","auth_message":"PIN or password"}
→ {"type":"post_auth_message_response","response":"123456"}
← {"type":"success"}
→ {"type":"start_session","cmd":["/usr/local/bin/rosella-session"],"env":[]}
```

`auth_message_type` is `secret`, `visible`, `info` or `error`; a failure comes
back as `{"type":"error","error_type":"auth_error",...}`, which is retryable,
not fatal. The greeter never sees a password hash — PAM does the work.

## PIN authentication

One text field that accepts **either** the PIN or the real password, so a
forgotten PIN is never a lockout.

`/etc/pam.d/gdm-password` today, and `/etc/pam.d/greetd` when greetd lands —
the same auth block, byte for byte:

```
auth     requisite     pam_faillock.so preauth
auth     required      pam_env.so
auth     required      pam_faildelay.so delay=2000000
auth     [success=2 default=ignore] pam_exec.so quiet expose_authtok /usr/local/libexec/rosella-pin
auth     [success=1 default=ignore] pam_unix.so nullok try_first_pass
auth     [default=die] pam_faillock.so authfail
auth     optional      pam_faillock.so authsucc
auth     [success=done ignore=ignore default=bad] pam_selinux_permit.so
auth     optional      pam_gnome_keyring.so
auth     include       postlogin
```

Why this exact shape — two of these are not stylistic:

- **`authfail` must never be reached with a success status.** It returns
  `PAM_PERM_DENIED` when it is, which rejects a correct password. The first
  draft used `sufficient` lines and `substack password-auth`, so a successful
  password ran into `pam_faillock authfail`: measured on Silver, a correct
  secret produced `pam_authenticate: 6 Permission denied`. The jumps fix it —
  PIN success skips the password leg, password success skips the failure line,
  and both land on `authsucc` so the tally is cleared.
- **`preauth` is `requisite`, not Fedora's `required`.** With `required` a
  failed preauth does not stop the stack, so a correct PIN typed while locked
  would still reach `authsucc` and clear the lock. Measured with `requisite`: a
  correct PIN while locked stays denied and the tally is unchanged.
- `pam_exec ... expose_authtok` hands the typed secret to the verifier on
  **stdin**; it never appears in `argv` or in `ps`.
- `pam_unix try_first_pass` means the same field accepts the password too, and
  a missing or broken verifier degrades to password login instead of locking
  the machine.
- `pam_env`, `pam_faildelay` and `pam_selinux_permit` are kept from GDM's own
  service file; the password leg is spelled out rather than `substack
  password-auth` so the control flags can jump over the failure line.

Rate limiting is not optional for a 4-digit PIN, so `pam_faillock` is
configured in `/etc/security/faillock.conf` (`deny = 5`, `unlock_time = 300`,
`fail_interval = 900`). Nothing else on Silver used `pam_faillock` before, so
the settings reach only this stack.

Fedora has no `pam_pwdfile` package, so a verifier is required either way.
Putting the PIN into `password-auth` instead would expose it to `sshd` and
`sudo`; this stack is scoped to the greeter alone.

`/usr/local/libexec/rosella-pin` is built from the repo: `tools/Rosella.Pin`,
a console app with P/Invoke only and no NuGet references. It is written and
tested; `--self-test` is the acceptance test.

```
rosella-pin                      verify the secret on stdin (this is what PAM calls)
rosella-pin --set                prompt twice, store a 4-12 digit PIN
rosella-pin --check              report state and hash prefix, never the hash
rosella-pin --self-test          hashing, verification, file modes, refusal paths
  --path FILE                    use FILE instead of /etc/rosella/pin
exit: 0 match, 1 mismatch, 2 error
```

Deliberate properties, each with a reason:

- **The secret only ever arrives on stdin or a terminal, never in `argv`**, so
  it cannot leak through `ps` or shell history. `--set` refuses to run when
  stdin is not a terminal, for the same reason.
- `--set` hashes with `crypt_gensalt_rn` (yescrypt, `$y$`) and **reads the file
  back and verifies against it** before reporting success.
- The stored file is replaced by writing a uniquely named temporary file
  created `0600`, `fsync`ing it, renaming it over the target and `fsync`ing the
  directory, so the change survives a power loss; a symlink at the target is
  refused, and nothing after the rename can fail and mis-report a write that
  already happened.
- Verification compares the full hash string in fixed time
  (`CryptographicOperations.FixedTimeEquals`).
- A malformed or unreadable stored hash exits **2**, not 1, so PAM falls
  through to the password instead of pretending the PIN was wrong.
- `crypt_checksalt` is consulted first, so an invalid salt or a disabled method
  is reported as 2 rather than as a failed attempt.

libxcrypt on Silver exports `crypt`, `crypt_gensalt_rn` and `crypt_checksalt`
(`libcrypt.so.2`, libxcrypt 4.5.2); the tool resolves `libcrypt.so.2` then
`libcrypt.so.1`, and `pam_unix` is already configured `yescrypt`, so the hash
format is the system's own. The non-reentrant `crypt()` is used on purpose:
this process verifies one secret and exits, which avoids depending on the
layout of `struct crypt_data` (it differs between libxcrypt and glibc).

### Tested, on Mercury and on Silver

`--self-test` 43/43 on both. The suite asserts the PAM-visible contract
directly: correct secret → 0, wrong → 1, empty/missing/unreadable/malformed →
2; a `$6$` hash produced by openssl (not by this tool) verifies; `$y$`, `$6$`,
`$5$` (legacy) and `$2b$` are all reported usable by `--check`, and an unknown
prefix is not; symlinks (existing, dangling, to a directory) are refused;
six/two/comment/whitespace/empty files are all errors rather than mismatches;
a loose `0644` file is re-pinned by the next write. Interactive `--set` driven
through a pty: stores `$y$`, mode `rw-------`, and the new PIN verifies
immediately after. Mismatched and too-short PINs are refused before anything is
written.

Rate limit: this is not optional. 4 digits is 10⁴, so `deny=5 /
unlock_time=300` gives ~5 guesses per 5 minutes — about 3 days to exhaust by
brute force, and the lockout is what makes a 4-digit PIN defensible at all.
Without `pam_faillock`, 4 digits falls to seconds. 6 is a reasonable middle
(10⁶ ≈ 8 months at that rate); 8 digits is effectively safe even without the
lockout.

### Lock screen

This is where the PIN pays for itself: with locking switched off for want of a
usable unlock, the machine is *less* safe than one that locks. The lock screen
verifies the same `/etc/rosella/pin` **in process** (same `crypt_r` call, no
PAM round trip, works even if greetd has been swapped) and is the M8 item whose
absence makes the current setup weaker than it looks.

## Keyring: password set to the PIN

Today the login keyring is unlocked by `pam_gnome_keyring auto_start` using the
authtok. With this stack the authtok is the PIN, so the keyring password must
be the PIN or every login prompts.

- Mechanism: unlock the keyring in a session, then change its password to the
  PIN. `seahorse` is the supported tool and is **not installed**; `libsecret`
  is installed but exports no `secret_service_change_password*` symbol on this
  build (checked with `nm -D`), so there is no scriptable route without it.
  Installing `seahorse` needs your agreement.
- Consequence, stated once: the login keyring becomes a 4–12 digit secret on an
  unencrypted btrfs volume with no TPM, so anyone holding the disk can brute
  force it offline. That is the price of never typing a password; keep the PIN
  unique to this machine, and prefer 8 digits over 4: 10^4 is seconds of GPU
  time offline, 10^8 is not.
- Second consequence: logging in with the *password* (the fallback) leaves the
  keyring locked, because the authtok no longer matches. One keyring prompt in
  that case only.

## Sequence

1. `tools/Rosella.Pin` + `/etc/rosella/pin`, and add the same two `auth` lines
   to `/etc/pam.d/gdm-password` **temporarily**, to prove the verifier on the
   greeter you already have. Reversible by deleting the lines.
2. Install `greetd` (+ `seahorse` if you want the keyring change); create
   `greeter`; `systemctl enable greetd`, `systemctl disable gdm`.
3. `--greeter` mode in `Rosella.Compositor`: IPC client, PIN field, session
   list, `ScalePolicy` scale, `Scene`/`Shell` drawing. This is the only step
   that touches code another session is editing (`Program.cs`), so it should be
   scheduled, not started ad hoc.
4. M8 lock screen with the same verifier.

## Trial and rollback

Do not make it the default to find out whether it works. Install greetd but
run it on **VT 3** with GDM still on VT 1:

```toml
[terminal]
vt = 3
```

`systemctl start greetd`, `Ctrl+Alt+F3` to try it, `Ctrl+Alt+F1` to go back.
Rollback is `systemctl disable --now greetd`; GDM was never touched. Only after
the greeter has survived a week does `vt = 1` + `disable gdm` make sense.

Failure modes to expect, all recoverable from another VT: greeter cannot open
`/dev/dri/card0` (missing `video`), cannot open evdev (missing `input`), or
`start_session` fails because `rosella-session` was not found under the
greeter's `PATH`.

## Applied on Silver (1 November 2026)

**Live and verified end to end.** Silver rebooted at 23:57 on 1 November, greetd
started the Rosella greeter on VT 1 as the `greeter` user, and a login succeeded
12 seconds later. No tracebacks, no SELinux denials (`ausearch -m avc -ts boot`
is empty), and the session it produced is a normal one on `tty1` with
`XDG_CURRENT_DESKTOP=Rosella:GNOME`, `XDG_SESSION_TYPE=wayland`. The greeter user
session lived from 23:57:29 to 23:57:51 — created when greetd started it,
torn down when the VT was handed to the real session.

| Piece | State |
|---|---|
| `/usr/local/libexec/rosella-pin` | installed; `--self-test` 44/44 |
| `/etc/rosella/pin` | yescrypt `$y$`, mode `0600 root:root` |
| `/etc/pam.d/gdm-password` | the auth block above |
| `/etc/security/faillock.conf` | `deny = 5`, `unlock_time = 300`, `fail_interval = 900` |
| `/etc/pam.d/gdm-password` | the auth block above; kept so switching back to GDM keeps the PIN |
| greetd | 0.10.3 installed, `enabled`; GDM `disabled`; `/etc/greetd/config.toml` runs `/usr/local/bin/rosella-greeter` on VT 1 as user `greeter` |
| `/etc/pam.d/greetd` | the same auth block as `gdm-password`, above |
| `greeter` user | `--system --no-create-home --shell /sbin/nologin`, groups `video,input,render` |
| `/etc/tmpfiles.d/rosella-greeter.conf` | private `/run/rosella-greeter`, `0700 greeter:greeter` |

The PIN needs **no** permission change on `/etc/rosella/pin`, contrary to what an
earlier revision of this document claimed. greetd does not run PAM as the
greeter: it re-execs itself as a session worker, and that worker keeps
**root** for `pam_start`, `pam_authenticate` and `pam_open_session`, dropping to
the target uid only in a further fork immediately before `execve`. So
`pam_exec` inherits root and reads the 0600 file exactly as
`gdm-session-worker` did. Confirmed on the installed binary (`session-worker`
strings present) and by running the real `greetd` service through the probe as
root: a correct PIN returned `pam_authenticate: 0 Success` and
`pam_acct_mgmt: 0 Success`. The file is still `0600 root:root`.

Verified against the real `gdm-password` service with a throwaway PAM probe
(`libpam` has no headers on Silver, so the probe declares its own; the probe and
the test services were removed afterwards):

- correct PIN → `pam_authenticate: 0 Success`, `pam_acct_mgmt: 0 Success`
- five wrong PINs → five tally entries, account locked
- correct PIN while locked → `7 Authentication failure`, tally unchanged
- `faillock --reset` → correct PIN succeeds and the tally is cleared
- wrong secret with the final file → rejected, exactly one tally entry

The same probe was then run against `greetd` and a `--path`-redirected copy of
its service file, with the same results (correct PIN → success on both auth and
account; five wrong → lockout; correct PIN while locked → denied with the tally
unchanged; reset → success). Rejections surface as `6 Permission denied` there
rather than `7`, because the failure is recorded by the last module in the
stack; the greeter treats any non-zero result the same way.

A temporary test PIN and a `--path`-redirected copy of the service were used so
the real PIN was never needed and never changed; `/etc/rosella/pin` is
byte-identical before and after (sha256 matches the backup).

Backups for rollback: `/root/gdm-password.backup`, `/root/rosella-pin.backup`,
`/root/faillock.conf.backup`. Restoring the first and third and running
`faillock --user michael --reset` returns the machine to its previous state.

To go back to GDM entirely:

```
sudo systemctl disable greetd && sudo systemctl enable gdm
```

Re-enable in that order — `systemctl enable` a display manager while another one
owns `/etc/systemd/system/display-manager.service` fails on the existing
symlink, and `disable` deletes it.

**Do not `systemctl start greetd` while a Rosella session is running.** The
greeter would start on VT 1 and grab the seat's evdev devices from the live
session, which is the same half-stolen-seat failure `Program.cs` guards against
within one session. The switch is done by rebooting, which is what was done.

**Recovery if the greeter misbehaves at boot.** The greeter grabs every evdev
device, so Ctrl+Alt+Fn never reaches the kernel console handler and there is no
local way back to a text console — only SSH (`silver.lan`, the `michael`
session's own logins are unaffected) followed by
`sudo systemctl disable greetd && sudo systemctl enable gdm`. This is the one
sharp edge of the current greeter and the reason the seat-manager follow-up in
`GreeterApp.cs` matters beyond convenience; a hung greeter otherwise means a
hard power cycle.

Two consequences to keep in mind. The login keyring's password is still the
account password, so logging in with the PIN leaves the keyring locked and
prompts once (see the keyring section). And the 4-digit case remains the weak
one: the lockout is what makes it defensible, so do not lower `deny` or
`unlock_time`.

## Cost

| Piece | Estimate |
|---|---|
| `tools/Rosella.Pin` (libcrypt P/Invoke, `--set`, verify, tests) | 1 day |
| PAM stack + `/etc/rosella/pin` + faillock config | 1 hour |
| `--greeter` mode: greetd IPC, PIN field, session list | 3 days |
| Lock screen sharing the verifier | 2 days |
| greetd cutover + trial | 1 hour |

## Not in this document

No kernel parameters, no reboots, no package installs, no edits under
`src/Rosella.Compositor/**` or `src/Rosella.Drm/**`. The nouveau blacklist
revert (`grubby --remove-args="rd.driver.blacklist=nouveau
modprobe.blacklist=nouveau"`) is independent and still waiting on a reboot you
choose.
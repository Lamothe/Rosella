# Live module reload

Use `dotnet run --project tools/Rosella.Driver -- modules` to compare running builds with the installed files.
For a taskbar-only change, publish with `scripts/deploy.sh --taskbar`, then run
`dotnet run --project tools/Rosella.Driver -- reload taskbar`. Report the result and active build ID. A same-build reload
reinitializes the module with its existing code. For dashboard widgets, publish with `scripts/deploy.sh --dashboard` and
use `reload dashboard` or `reload <widget ID>` with the same driver. The desktop UI exposes reload in dashboard **Settings → Modules**.
Changes to shared compositor code take effect on the next Rosella login after a full publish.
If `modules` returns an unknown-command error, the running build predates the status command. Older reload-capable
builds accept `reload-taskbar` and return a generic acknowledgement.

# Live module reload

Use `dotnet run --project tools/Rosella.Driver -- modules` to compare the running taskbar build with the installed file.
For a taskbar-only change, publish with `scripts/deploy.sh --taskbar`, then run
`dotnet run --project tools/Rosella.Driver -- reload taskbar`. Report the result and active build ID. A same-build reload
reinitializes the module with its existing code. The desktop UI exposes the same action in Quick Settings under **Modules**.
Changes to shared compositor code take effect on the next Rosella login after a full publish.
If `modules` returns an unknown-command error, the running build predates the status command. Older reload-capable
builds accept `reload-taskbar` and return a generic acknowledgement.

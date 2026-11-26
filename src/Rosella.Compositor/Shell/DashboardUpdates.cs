using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Rosella.Compositor;

/// <summary>Reads recent, successful package transactions without refreshing repositories.</summary>
internal sealed class DashboardUpdates(bool enabled = false)
{
    private readonly object _gate = new();
    private DateTimeOffset _refreshAfter;
    private volatile NotificationRecord[] _items = [];
    internal IReadOnlyList<NotificationRecord> Items => _items;
    internal Action? Dirty { get; set; }

    internal void Refresh()
    {
        if (!enabled)
        {
            return;
        }
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow < _refreshAfter)
            {
                return;
            }
            _refreshAfter = DateTimeOffset.UtcNow.AddMinutes(2);
        }
        _ = Task.Run(LoadAsync);
    }

    private async Task LoadAsync()
    {
        var result = new List<NotificationRecord>();
        var rpm = await QueryAsync("dnf5", ["-C", "history", "info", "--json", "last-8..last"])
            ?? await QueryAsync("dnf5", ["-C", "history", "info", "--json", "last"]);
        if (rpm is not null)
        {
            try { result.AddRange(ParseDnf(rpm)); }
            catch (JsonException) { }
        }

        // Calendar-day grouping keeps a batch of app updates in one entry.
        var today = DateOnly.FromDateTime(DateTime.Today);
        var since = today.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var installation in new[] { "--user", "--system" })
        {
            var flatpak = await QueryAsync("flatpak", ["history", installation, "--json", "--columns=time,change,application,ref", "--since=" + since]);
            if (flatpak is not null)
            {
                try { result.AddRange(ParseFlatpak(flatpak, today)); }
                catch (JsonException) { }
            }
        }
        _items = [.. result.OrderByDescending(item => item.Time).Take(12)];
        Dirty?.Invoke();
    }

    private static async Task<string?> QueryAsync(string executable, string[] arguments)
    {
        var launch = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        launch.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) { launch.ArgumentList.Add(argument); }

        try
        {
            using var process = Process.Start(launch);
            if (process is null) { return null; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(stdout, stderr);
                return process.ExitCode == 0 ? stdout.Result : null;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) { process.Kill(); }
                await process.WaitForExitAsync();
                return null;
            }
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static IEnumerable<NotificationRecord> ParseDnf(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) { yield break; }
        foreach (var transaction in document.RootElement.EnumerateArray())
        {
            if (StringAt(transaction, "status") != "Ok" ||
                !transaction.TryGetProperty("end_time", out var time) || time.ValueKind != JsonValueKind.Number ||
                !time.TryGetInt64(out var seconds) || seconds is <= 0 or > 253402300799 ||
                !transaction.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            var names = packages.EnumerateArray().Where(package => StringAt(package, "action") == "Upgrade")
                .Select(package => PackageName(StringAt(package, "nevra"))).Where(name => name.Length > 0).ToArray();
            if (names.Length != 0)
            {
                yield return new("System updates", names.Length == 1 ? "1 package updated" : $"{names.Length} packages updated",
                    Examples(names), DateTimeOffset.FromUnixTimeSeconds(seconds));
            }
        }
    }

    internal static IEnumerable<NotificationRecord> ParseFlatpak(string json, DateOnly today)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) { yield break; }
        var updates = new List<(DateTimeOffset Time, string App)>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var id = StringAt(entry, "application");
            if (StringAt(entry, "change") != "deploy update" || id.Length == 0 ||
                !StringAt(entry, "ref").StartsWith("app/", StringComparison.Ordinal) ||
                !DateTime.TryParseExact(StringAt(entry, "time"), "MMM d HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var local))
            {
                continue;
            }

            var year = local.Month > today.Month ? today.Year - 1 : today.Year;
            if (local.Day <= DateTime.DaysInMonth(year, local.Month))
            {
                var time = new DateTime(year, local.Month, local.Day, local.Hour, local.Minute, local.Second, DateTimeKind.Local);
                updates.Add((new DateTimeOffset(time), id.Split('.')[^1]));
            }
        }
        foreach (var day in updates.GroupBy(update => update.Time.Date))
        {
            var names = day.Select(update => update.App).Distinct(StringComparer.Ordinal).ToArray();
            yield return new("Flatpak", names.Length == 1 ? "1 Flatpak updated" : $"{names.Length} Flatpaks updated",
                Examples(names), day.Max(update => update.Time));
        }
    }

    private static string StringAt(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string PackageName(string nevra)
    {
        var release = nevra.LastIndexOf('-');
        var version = release > 0 ? nevra.LastIndexOf('-', release - 1) : -1;
        return version > 0 ? nevra[..version] : nevra;
    }

    private static string Examples(string[] names)
    {
        var distinct = names.Distinct(StringComparer.Ordinal).ToArray();
        var sample = string.Join(", ", distinct.Take(3));
        return distinct.Length > 3 ? sample + "…" : sample;
    }
}

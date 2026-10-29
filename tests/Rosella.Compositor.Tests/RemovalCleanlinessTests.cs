namespace Rosella.Compositor.Tests;

/// <summary>
/// Removal cleanliness: the menu-based launcher and its Super binding are
/// gone, and no product source or doc still refers to them. The scan
/// covers src/, README.md and docs/ — this test's own fixtures are exempt
/// because they are the forbidden strings.
/// </summary>
public class RemovalCleanlinessTests
{
    private static readonly string[] ForbiddenTokens =
    [
        "Start menu",
        "Super tap",
        "SuperChord",
        "ToggleLauncher",
        "CloseLauncher",
        "RestoreLauncherFocus",
        "class Launcher",
        "Shell.Launcher",
        "shell.Launcher",
        "Launcher.Apps",
        "Launcher.CreateStartInfo",
        "Launcher.SpawnOverride",
        "Launcher.PanelRect",
        "Launcher.IsOpen",
        "Launcher.Toggle",
        "Launcher.HandleKey",
    ];

    [Fact]
    public void ProductSourcesAndDocs_DoNotMentionTheDeletedMenuOrSuperBinding()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        foreach (var path in FilesToScan(root))
        {
            var text = File.ReadAllText(path);
            foreach (var token in ForbiddenTokens)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(root, path)}: {token}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheMenuLauncherSourceFile_IsGone()
    {
        var shellDir = Path.Combine(RepoRoot(), "src", "Rosella.Compositor", "Shell");
        Assert.False(File.Exists(Path.Combine(shellDir, "Launcher.cs")));
        Assert.Empty(Directory.GetFiles(shellDir, "Launcher*.cs"));
    }

    private static IEnumerable<string> FilesToScan(string root)
    {
        foreach (var path in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            yield return path;
        }

        foreach (var path in Directory.GetFiles(Path.Combine(root, "tools"), "*.cs", SearchOption.AllDirectories))
        {
            yield return path;
        }

        yield return Path.Combine(root, "README.md");
        foreach (var path in Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories))
        {
            yield return path;
        }
    }

    /// <summary>Walks up from the test binaries to the repository root.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Rosella.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("repository root not found above the test binaries");
    }
}

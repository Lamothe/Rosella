namespace Rosella.Compositor.Tests;

/// <summary>Locates repository files from the test binaries.</summary>
internal static class RepoFiles
{
    /// <summary>The repository root, found by walking up from the test output.</summary>
    internal static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "scripts", "rosella-askpass")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new FileNotFoundException("scripts/rosella-askpass not found above the test directory");
        }
    }

    internal static string AskpassScript => Path.Combine(Root, "scripts", "rosella-askpass");
}

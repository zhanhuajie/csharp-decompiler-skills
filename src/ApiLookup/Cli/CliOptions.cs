namespace ApiLookup;

public sealed class CliOptions
{
    public string Command { get; set; } = string.Empty;
    public List<string> Positional { get; } = new();

    /// <summary>Canonical option name -> first spelling the user typed (e.g. "namespace" -> "--ns"). Used for per-command validation.</summary>
    public Dictionary<string, string> SpecifiedOptions { get; } = new(StringComparer.Ordinal);

    // Sources / Index selection
    public string? IndexName { get; set; }
    public string? DbPath { get; set; }
    public List<string> Projects { get; } = new();
    public List<string> Dirs { get; } = new();
    public List<string> Dlls { get; } = new();
    public List<string> IncludePatterns { get; } = new();
    public List<string> ExcludePatterns { get; } = new();
    public bool Recursive { get; set; } = true;
    public bool IncludeNonPublic { get; set; }
    public bool ForceRebuild { get; set; }
    public string? TargetFramework { get; set; }

    // Index management
    public bool ListIndexes { get; set; }
    public string? DropIndex { get; set; }
    public bool PruneAuto { get; set; }

    // Query filters
    public string? KindFilter { get; set; }
    public string? NamespaceFilter { get; set; }
    public string? AssemblyFilter { get; set; }
    public string? MemberFilter { get; set; }
    public string Role { get; set; } = "both";
    public int? Limit { get; set; }
    public bool IncludeInherited { get; set; }
    public bool IncludeExtensions { get; set; }
    public bool ShowFullDocs { get; set; }
    public bool ListRefs { get; set; }
    public bool Json { get; set; }
    public bool Verbose { get; set; }
    public bool ShowHelp { get; set; }
    public bool ShowVersion { get; set; }
}

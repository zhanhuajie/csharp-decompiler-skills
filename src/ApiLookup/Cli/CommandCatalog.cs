namespace ApiLookup;

internal enum PositionalArity
{
    None,
    ExactlyOne,
    OneOrMore,
}

/// <summary>Everything a command handler needs: parsed options plus the output streams.</summary>
internal sealed record CommandContext(CliOptions Options, TextWriter Out, TextWriter Err);

/// <summary>Declarative description of a CLI command: its name, accepted options, positional arity, and handler.</summary>
internal sealed record CommandSpec(
    string Name,
    PositionalArity Arity,
    string ArgumentName,
    IReadOnlySet<string> AllowedOptions,
    Func<CommandContext, int> Handler);

/// <summary>Single source of truth for which commands exist and which options each one accepts.</summary>
internal static class CommandCatalog
{
    public static readonly string[] GlobalOptions = { "help", "version", "json", "verbose" };

    public static readonly string[] DirectSourceOptions =
        { "project", "dir", "dll", "include", "exclude", "no-recursive", "include-nonpublic", "tfm" };

    public static readonly string[] SourceOptions =
        { "index", "db", "project", "dir", "dll", "include", "exclude", "no-recursive", "include-nonpublic", "force", "tfm" };

    public static readonly string[] IndexManagementOptions = { "list", "drop", "prune" };

    public static readonly IReadOnlyList<CommandSpec> All = new[]
    {
        Spec("index", PositionalArity.None, "", CommandHandlers.RunIndex, SourceOptions, IndexManagementOptions),
        Spec("search", PositionalArity.OneOrMore, "<query>", CommandHandlers.RunSearch,
            SourceOptions, new[] { "namespace", "assembly", "kind", "limit", "list-refs" }),
        Spec("show", PositionalArity.ExactlyOne, "<TypeName>", CommandHandlers.RunShow,
            SourceOptions, new[] { "namespace", "assembly", "member", "inherited", "ext", "docs" }),
        Spec("hierarchy", PositionalArity.ExactlyOne, "<TypeName>", CommandHandlers.RunHierarchy,
            SourceOptions, new[] { "namespace", "assembly", "limit" }),
        Spec("by-type", PositionalArity.ExactlyOne, "<TypeName>", CommandHandlers.RunByType,
            SourceOptions, new[] { "namespace", "assembly", "role", "limit" }),
        Spec("refs", PositionalArity.None, "", CommandHandlers.RunRefs, SourceOptions),
        Spec("locate", PositionalArity.ExactlyOne, "<TypeName>", CommandHandlers.RunLocate,
            DirectSourceOptions, new[] { "namespace", "assembly", "limit", "list-refs" }),
    };

    private static readonly Dictionary<string, CommandSpec> ByName = BuildLookup();

    /// <summary>All command names (used for typo suggestions).</summary>
    public static IEnumerable<string> AllNames => ByName.Keys;

    public static bool TryGet(string name, out CommandSpec spec)
        => ByName.TryGetValue(name, out spec!);

    private static CommandSpec Spec(
        string name,
        PositionalArity arity,
        string argumentName,
        Func<CommandContext, int> handler,
        params string[][] optionGroups)
    {
        var allowed = new HashSet<string>(GlobalOptions, StringComparer.Ordinal);
        foreach (var group in optionGroups) allowed.UnionWith(group);
        return new CommandSpec(name, arity, argumentName, allowed, handler);
    }

    private static Dictionary<string, CommandSpec> BuildLookup()
    {
        var map = new Dictionary<string, CommandSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in All)
            map.Add(spec.Name, spec);
        return map;
    }
}

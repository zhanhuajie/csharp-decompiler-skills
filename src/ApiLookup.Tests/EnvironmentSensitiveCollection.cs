using Xunit;

namespace ApiLookup.Tests;

/// <summary>
/// Tests that mutate process-wide state (environment variables such as APILOOKUP_CACHE_DIR / NUGET_PACKAGES)
/// must not run in parallel with each other or with any other test class.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentSensitiveCollection
{
    public const string Name = "EnvironmentSensitive";
}

namespace Sattle.Tests;

/// <summary>What a test needs from the machine that is not in the repository. A test whose need is not met is reported as skipped with the reason, never as passed.</summary>
static class Requires
{
    public static string? Cosmos => StoreConformanceTests.CosmosEndpoint is null ? "SATTLE_COSMOS_EMULATOR is not set (the Cosmos DB emulator is not running)" : null;
}

/// <summary>A test against the Cosmos DB emulator.</summary>
public sealed class CosmosFactAttribute : FactAttribute
{
    public CosmosFactAttribute() { Skip = Requires.Cosmos; }
}

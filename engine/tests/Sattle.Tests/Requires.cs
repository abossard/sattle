namespace Sattle.Tests;

/// <summary>What a test needs from the machine that is not in the repository. A test whose need is not met is reported as skipped with the reason, never as passed.</summary>
static class Requires
{
    public static string? Capture => Directory.Exists(Repo.Captures) && Directory.EnumerateDirectories(Repo.Captures).Any() ? null : "no capture under lab/captures (the captures are not in the repository)";
    public static string? CaptureWithOverlay => Capture ?? (Repo.Overlay is null ? "no lab/captures/space-overlay.sattle (the workload arguments of the real captures are not in the repository)" : null);
    public static string? Cosmos => StoreConformanceTests.CosmosEndpoint is null ? "SATTLE_COSMOS_EMULATOR is not set (the Cosmos DB emulator is not running)" : null;
}

/// <summary>A test on the real captures.</summary>
public sealed class CaptureFactAttribute : FactAttribute
{
    public CaptureFactAttribute(bool overlay = false) { Skip = overlay ? Requires.CaptureWithOverlay : Requires.Capture; }
}

/// <summary>A theory on the real captures.</summary>
public sealed class CaptureTheoryAttribute : TheoryAttribute
{
    public CaptureTheoryAttribute() { Skip = Requires.Capture; }
}

/// <summary>A test against the Cosmos DB emulator.</summary>
public sealed class CosmosFactAttribute : FactAttribute
{
    public CosmosFactAttribute() { Skip = Requires.Cosmos; }
}

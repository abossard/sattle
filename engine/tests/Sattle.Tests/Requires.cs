namespace Sattle.Tests;

/// <summary>What a test needs from the machine that is not in the repository. A test whose need is not met is reported as skipped with the reason, never as passed.</summary>
static class Requires
{
    public static string? Capture => Repo.CaptureDirs.Count > 0 ? null : "no capture under lab/captures (the captures are not in the repository)";
    public static string? CaptureWithOverlay => Capture ?? (Repo.Overlay is null ? "no lab/captures/space-overlay.sattle (the workload arguments of the real captures are not in the repository)" : null);
    public static string? Fixtures => Directory.Exists(Repo.Fixtures) && Directory.EnumerateFiles(Repo.Fixtures, "*.ndjson", SearchOption.AllDirectories).Any() ? null : "no data under lab/fixtures (derived from a real capture and generated locally with lab/fixtures/build.sh; not in the repository)";
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

/// <summary>A test on `lab/fixtures`, the scrubbed excerpt of a real capture. What it proves that does not need real data has a twin on the synthetic estate.</summary>
public sealed class FixturesFactAttribute : FactAttribute
{
    public FixturesFactAttribute() { Skip = Requires.Fixtures; }
}

/// <summary>A theory on `lab/fixtures`.</summary>
public sealed class FixturesTheoryAttribute : TheoryAttribute
{
    public FixturesTheoryAttribute() { Skip = Requires.Fixtures; }
}

/// <summary>A test against the Cosmos DB emulator.</summary>
public sealed class CosmosFactAttribute : FactAttribute
{
    public CosmosFactAttribute() { Skip = Requires.Cosmos; }
}

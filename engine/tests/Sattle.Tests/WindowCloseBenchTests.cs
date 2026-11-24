using System.Diagnostics;

namespace Sattle.Tests;

public class WindowCloseBenchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reports_initial_population_and_counts_quiet_source_windows(bool withMetric)
    {
        var capture = Directory.CreateTempSubdirectory("sattle-close-");
        try
        {
            // Six consecutive minute markers: the heartbeats of the Kubernetes row types at second 59. One optional reading belongs to the first minute and closes at the third advance.
            var records = Path.Combine(EstateTests.EstateDir, "records", "records.ndjson");
            var markers = File.ReadLines(records)
                .Where(line => line.Contains("\"payload\":{}", StringComparison.Ordinal) && line.Contains(":59.000000000Z\"", StringComparison.Ordinal))
                .GroupBy(line => line[line.IndexOf("\"capturedAt\"", StringComparison.Ordinal)..line.IndexOf("\"seq\"", StringComparison.Ordinal)]).Take(6).SelectMany(g => g);
            File.WriteAllLines(Path.Combine(capture.FullName, "markers.ndjson"), markers);
            if (withMetric)
                File.WriteAllLines(Path.Combine(capture.FullName, "metric.ndjson"), File.ReadLines(records).Where(line => line.Contains("\"source\":\"records:ResourceMetricRow\"", StringComparison.Ordinal) && line.Contains("\"values\"", StringComparison.Ordinal)).Take(1));

            var child = await Process.RunAndCaptureTextAsync("dotnet",
                [typeof(Cli.Program).Assembly.Location, "bench", "phases", "--timing-only", "--capture", capture.FullName, "--defs", Repo.Definitions]);
            Assert.True(child.ExitStatus.ExitCode == 0, child.StandardError);
            var report = child.StandardOutput;
            Assert.Contains("window close all: 7 closes", report);
            Assert.Contains($"window close before telemetry: {(withMetric ? 2 : 6)} closes", report);
            Assert.Contains($"window close first populated: {(withMetric ? "1 close," : "0 closes")}", report);
            Assert.Contains($"window close steady (after first populated): {(withMetric ? 3 : 0)} closes", report);
            Assert.Contains("window close empty end-of-capture advance: 1 close,", report);
        }
        finally { capture.Delete(recursive: true); }
    }
}

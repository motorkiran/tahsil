using Tahsil.Application.Common;
using Tahsil.Server.Diagnostics;

namespace Tahsil.Server.Tests.Diagnostics;

[Trait("Requirement", "FND-44")]
public sealed class TelemetryTests
{
    [Fact]
    public void TheServiceVersion_IsTheReleaseVersion()
    {
        TelemetryExtensions.ServiceVersion().ShouldBe("0.1.0");
    }

    [Fact]
    public void TahsilsOwnSignals_AreNamedTahsil()
    {
        TahsilDiagnostics.Meter.Name.ShouldBe("Tahsil");
        TahsilDiagnostics.ActivitySource.Name.ShouldBe("Tahsil");
    }
}

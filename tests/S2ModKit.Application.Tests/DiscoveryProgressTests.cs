using S2ModKit.Application;

namespace S2ModKit.Application.Tests;

public sealed partial class PreciseComponentDiscoveryServiceTests
{
    [Fact]
    public async Task EllipsoidDiscoveryReportsEveryCandidateOfBothStagesWithoutChangingTheResult()
    {
        var fixture = CreateGunFixture(3);
        var reports = new List<DiscoveryProgress>();
        var token = TestContext.Current.CancellationToken;
        var silent = await new ExperimentalComponentDiscoveryService(null, null).DiscoverEllipsoidAsync(fixture.Input, fixture.Model, token);
        var reported = await new ExperimentalComponentDiscoveryService(null, null, new RecordingProgress(reports)).DiscoverEllipsoidAsync(fixture.Input, fixture.Model, token);

        var total = silent.Candidates.Count;
        Assert.True(total > 0);
        Assert.Equal(JsonDefaults.Serialize(silent), JsonDefaults.Serialize(reported));
        Assert.Equal(Enumerable.Range(0, total).Select(i => new DiscoveryProgress("experimental", i, total))
            .Concat(Enumerable.Range(0, total).Select(i => new DiscoveryProgress("ellipsoid", i, total))), reports);
    }

    private sealed class RecordingProgress(List<DiscoveryProgress> reports) : IProgress<DiscoveryProgress>
    {
        public void Report(DiscoveryProgress value) => reports.Add(value);
    }
}

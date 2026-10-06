using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;

namespace S2ModKit.Application.Tests;

public sealed class DirectionalAuthoringPublicationTests
{
    [Fact]
    public async Task SourceReportPublicationIsContentAddressedIdempotentAndNeverReplacesConflictingProof()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2modkit-directional-source-" + Guid.NewGuid().ToString("N"));
        var hash = ContentHash.Compute("synthetic-source"u8);
        var bone = new DirectionalBoneAssertion { Version = 1, AssertionId = "fixed", BoneName = "source_bone", BoneIndex = 0, RootSkeletonHash = hash, Lods = [new(0, hash, 1)] };
        var source = new DirectionalAuthoringSource(hash, [new("member-source", [new(0, ["dc_0123456789abcdef01234567"], 3)])], [], [], [new("source_bone", 0, bone, null)]);
        var token = TestContext.Current.CancellationToken;
        try
        {
            var first = await FileSystemDirectionalAuthoringPublisher.PublishAsync(root, source, token);
            using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(first.Path, token));
            var assertion = report.RootElement.GetProperty("source").GetProperty("bones")[0].GetProperty("assertion");
            Assert.Equal("root_bone_contributors", assertion.GetProperty("kind").GetString());
            Assert.IsType<DirectionalBoneAssertion>(JsonDefaults.Deserialize<DirectionalProtectionAssertion>(System.Text.Encoding.UTF8.GetBytes(assertion.GetRawText()), "Source assertion candidate"));
            Assert.Equal(first.Hash, ContentHash.Compute(await File.ReadAllBytesAsync(first.Path, token)));
            Assert.Equal(first, await FileSystemDirectionalAuthoringPublisher.PublishAsync(root, source, token));
            await File.WriteAllTextAsync(first.Path, "conflicting-proof", token);
            var failure = await Assert.ThrowsAsync<S2ModKitException>(() => FileSystemDirectionalAuthoringPublisher.PublishAsync(root, source, token));
            Assert.Equal("DIRECTIONAL_REPORT_OUTPUT_CONFLICT", failure.Error.Code);
            Assert.Equal("conflicting-proof", await File.ReadAllTextAsync(first.Path, token));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

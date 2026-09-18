using S2ModKit.Domain;

namespace S2ModKit.Domain.Tests;

public sealed class StructuralCompatibilityTests
{
    [Fact]
    public void StructuralSignatureIsCanonicalAndEnumerationIndependent()
    {
        var first = StructuralSignature.Create(
            "Source2.Root-Model",
            [new StructuralFact("skinning", "weighted"), new StructuralFact("storage", "mvtx_midx")]);
        var second = StructuralSignature.Create(
            "source2.root-model",
            [new StructuralFact("storage", "mvtx_midx"), new StructuralFact("skinning", "weighted")]);

        Assert.Equal(first.SignatureId, second.SignatureId);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal("source2.root-model", first.Family);
        Assert.Equal(["skinning", "storage"], first.Facts.Select(static fact => fact.Key));
        Assert.Equal(first.Facts, second.Facts);
        Assert.Matches("^sig_[0-9a-f]{24}$", first.SignatureId);
    }

    [Fact]
    public void StructuralSignatureRejectsDuplicateFactsAndForgedIdentity()
    {
        Assert.Throws<ArgumentException>(() => StructuralSignature.Create(
            "source2.model",
            [new StructuralFact("storage", "mvtx"), new StructuralFact("STORAGE", "mbuf")]));

        var valid = StructuralSignature.Create("source2.model", [new StructuralFact("storage", "mvtx")]);
        Assert.Throws<ArgumentException>(() => new StructuralSignature(
            valid.ContractVersion,
            valid.Family,
            "sig_000000000000000000000000",
            valid.Fingerprint,
            valid.Facts));
    }

    [Fact]
    public void AssessmentCanonicalizesProfilesOperationsAndReasons()
    {
        var secondReason = new StructuralCompatibilityReason("Z_LAST", "Later detail.");
        var firstReason = new StructuralCompatibilityReason("A_FIRST", "Primary detail.");
        var assessment = new StructuralCompatibilityAssessment(
            StructuralSignature.Create("source2.model", [new StructuralFact("storage", "mvtx")]),
            [
                new StructuralProfileMatch(
                    new StructuralProfileIdentity("source2.weighted", 2),
                    StructuralCompatibilityContract.Rejected,
                    [secondReason, firstReason]),
                new StructuralProfileMatch(
                    new StructuralProfileIdentity("source2.weighted", 1),
                    StructuralCompatibilityContract.Matched,
                    [new StructuralCompatibilityReason("PROFILE_MATCHED", "All predicates passed.")]),
            ],
            [
                new OperationCompatibility(
                    "transform_component",
                    1,
                    CapabilityAvailability.Available,
                    [new StructuralCompatibilityReason("OPERATION_AVAILABLE", "The profile is supported.")]),
                new OperationCompatibility(
                    "remove_component",
                    1,
                    CapabilityAvailability.Available,
                    [new StructuralCompatibilityReason("OPERATION_AVAILABLE", "The profile is supported.")]),
            ]);

        Assert.Equal([1, 2], assessment.ProfileMatches.Select(static match => match.Profile.Version));
        Assert.Equal(["A_FIRST", "Z_LAST"], assessment.ProfileMatches[1].Reasons.Select(static reason => reason.Code));
        Assert.Equal(
            ["remove_component", "transform_component"],
            assessment.OperationCapabilities.Select(static capability => capability.OperationKind));
    }

    [Fact]
    public void CompatibilityContractsRejectUnknownStatusesAndDuplicateIdentities()
    {
        var reason = new StructuralCompatibilityReason("PROFILE_REJECTED", "A predicate failed.");
        var profile = new StructuralProfileIdentity("source2.profile", 1);

        Assert.Throws<ArgumentException>(() => new StructuralProfileMatch(profile, "maybe", [reason]));
        Assert.Throws<ArgumentException>(() => new OperationCompatibility("remove_component", 1, "maybe", [reason]));
        Assert.Throws<ArgumentException>(() => new StructuralCompatibilityAssessment(
            StructuralSignature.Create("source2.model", [new StructuralFact("storage", "mvtx")]),
            [
                new StructuralProfileMatch(profile, StructuralCompatibilityContract.Rejected, [reason]),
                new StructuralProfileMatch(profile, StructuralCompatibilityContract.Rejected, [reason]),
            ],
            []));
    }
}

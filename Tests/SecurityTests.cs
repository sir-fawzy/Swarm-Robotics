using Xunit;
using SwarmSecureCoordination.Core;
using SwarmSecureCoordination.Security;
using SwarmSecureCoordination.Models;
using SwarmSecureCoordination.Interfaces;
using System.Globalization;
public class SecurityTests
{
    readonly IdentityManager ids = new();
    readonly IMessageSigner signer;
    readonly BudgetManager budget = new(3);
    readonly MessageVerifier verifier;
    readonly SecureCoordinationLayer layer;
    public SecurityTests()
    {
        signer = ids.Provision("R1", true); verifier = new(ids);
        budget.InitializeRound(1, new() { "R1" });
        layer = new(verifier, budget, new HashLog(), new FilterModule(), new ConsensusModule(), 20);
    }
    Message Make(int round = 1) => new FaultyRobot(signer, 0.7, 0.7).CreateMessage(round, "GENESIS");
    [Fact] public void ValidConcurrentSnapshotsAndReplay()
    {
        var a = Make(); var b = Make();
        Assert.Equal(0.7, layer.Process(new[] { a, b }, 1)!.Value, 10);
        Assert.Null(layer.Process(new[] { a }, 1));
        budget.InitializeRound(2, new() { "R1" });
        Assert.Null(layer.Process(new[] { a }, 2));
        Assert.Equal(0.7, layer.Process(new[] { Make(2) }, 2)!.Value, 10);
        Assert.Null(layer.Process(new[] { a }, 1));
    }
    [Fact] public void EveryAuthenticatedFieldRejectsTampering()
    {
        var m = Make();
        foreach (var changed in new[] { m with { RobotId = "R2" }, m with { RoundId = 2 },
            m with { Value = 0.700000001 }, m with { MessageId = Guid.NewGuid() },
            m with { Timestamp = m.Timestamp.AddTicks(1) }, m with { PrevHash = new string('A', 64) },
            m with { Signature = new string('0', 64) } }) Assert.False(verifier.Verify(changed, changed.RoundId));
        Assert.Null(layer.Process(new[] { m with { Signature = new string('0', 64) } }, 1));
        Assert.Equal(0.7, layer.Process(new[] { m }, 1)!.Value, 10);
    }
    [Fact] public void MalformedAndNonFinite()
    {
        var m = Make();
        foreach (var bad in new[] { m with { Value = double.NaN }, m with { Value = double.PositiveInfinity },
            m with { Value = double.NegativeInfinity }, m with { Value = -1 }, m with { Value = 2 },
            m with { RobotId = null! }, m with { RobotId = new string('x', 65) }, m with { RobotId = "a|b" },
            m with { MessageId = Guid.Empty }, m with { Timestamp = DateTime.Now },
            m with { Timestamp = DateTime.UtcNow.AddHours(-1) }, m with { Timestamp = DateTime.UtcNow.AddHours(1) },
            m with { PrevHash = null! }, m with { Signature = new string('z', 64) } }) Assert.False(verifier.Verify(bad, 1));
        Assert.False(verifier.Verify(null!, 1)); Assert.False(verifier.Verify(m, 0));
        Assert.Null(layer.Process(Array.Empty<Message>(), 1));
    }
    [Fact] public void NoPrematureReplayConsumptionOrBudgetReset()
    {
        var m = Make(2);
        Assert.Null(layer.Process(new[] { m }, 2)); // no round 2 budget yet
        budget.InitializeRound(2, new() { "R1" });
        Assert.Equal(0.7, layer.Process(new[] { m }, 2)!.Value, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.InitializeRound(2, new() { "R1" }));
    }
    [Fact] public void FloodIsBoundedAcrossCalls()
    {
        int enumerated = 0;
        IEnumerable<Message> Flood() { while (true) { enumerated++; yield return null!; } }
        Assert.Null(layer.Process(Flood(), 1)); Assert.Equal(20, enumerated);
        Assert.Null(layer.Process(Flood(), 1)); Assert.Equal(20, enumerated);
    }
    [Fact] public void KeyPossessionDoesNotGrantAdmission()
    {
        var fake = ids.Provision("S1", false);
        var m = new FaultyRobot(fake, 1, 1).CreateMessage(1, "GENESIS");
        Assert.True(ids.VerifySignature(m)); Assert.False(verifier.Verify(m, 1));
        var fakeBudget = new BudgetManager(); fakeBudget.InitializeRound(1, new() { "S1" });
        var protectedLayer = new SecureCoordinationLayer(verifier, fakeBudget, new HashLog(), new FilterModule(), new ConsensusModule());
        Assert.Null(protectedLayer.Process(new[] { m }, 1));
        Assert.Throws<InvalidOperationException>(() => fake.Sign(Make()));
        Assert.Null(layer.Process(new[] { m }, 1)); Assert.Equal(0.7, layer.Process(new[] { Make() }, 1)!.Value, 10);
    }
    [Fact] public void CultureIndependentAndRandomKeys()
    {
        var m = Make(); var old = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.True(verifier.Verify(m, 1)); }
        finally { CultureInfo.CurrentCulture = old; }
        var other = new IdentityManager().Provision("R1", true);
        Assert.NotEqual(signer.Sign(m), other.Sign(m));
    }
    [Fact] public void BudgetsApplyOnlyAfterVerification()
    {
        var m = Make();
        layer.Process(Enumerable.Repeat(m with { Signature = new string('0', 64) }, 4), 1);
        Assert.Equal(0.7, layer.Process(new[] { m, Make(), Make() }, 1)!.Value, 10);
        Assert.Null(layer.Process(new[] { Make() }, 1));
    }
}

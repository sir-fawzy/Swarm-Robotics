using System.Globalization;
using System.Text;
using System.Text.Json;
using SwarmSecureCoordination.Distributed;
using Xunit;

public sealed class DistributedAgreementTests
{
    // Trusted experiment setup owns the credentials. Individual node code sees only its own signer.
    private sealed class Swarm : IDisposable
    {
        public NodeIdentity[] Keys { get; }
        public AgreementNode[] Nodes { get; }
        public SimulatedNetwork Network { get; }
        public SwarmMembership Membership { get; }
        public Guid Session { get; } = Guid.NewGuid();
        public HashSet<string> Silent { get; } = new(StringComparer.Ordinal);
        public Swarm(int n = 4, int f = 1, int maxIncoming = 4096)
        {
            Keys = Enumerable.Range(1, n).Select(i => new NodeIdentity($"R{i}")).ToArray();
            Membership = new(Keys.Select(k => KeyValuePair.Create(k.Id, k.PublicKey)), f);
            Network = new(seed: 42);
            Nodes = Keys.Select(k => new AgreementNode(k, Membership, Session, Network, maxIncomingPerRound: maxIncoming)).ToArray();
            foreach (var node in Nodes) Network.Register(node.Id, p => { if (!Silent.Contains(node.Id)) node.Receive(p); });
        }
        public void Begin(int round = 1)
        { for (int i = 0; i < Nodes.Length; i++) Nodes[i].BeginRound(round, 0.68 + i * 0.01); }
        public void Finish()
        { Network.Run(2); foreach (var node in Nodes) if (!Silent.Contains(node.Id)) node.Propose(); Network.Run(12); }
        public SignedReading Reading(int key, double value, int round = 1) => Keys[key].Sign(new SignedReading {
            Session = Session, Membership = Membership.Fingerprint, Round = round, Sender = Keys[key].Id,
            Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Value = value });
        public AgreementMessage Message(int key, AgreementKind kind, string digest = "", SignedReading[]? readings = null, SignedReading? reading = null, int round = 1)
            => Keys[key].Sign(new AgreementMessage { Kind = kind, Session = Session, Membership = Membership.Fingerprint,
                Round = round, Sender = Keys[key].Id, Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow,
                Digest = digest, Readings = readings ?? [], Reading = reading });
        public void Inject(AgreementMessage m, params int[] recipients)
        { foreach (var i in recipients) Network.Send(m.Sender, Nodes[i].Id, ProtocolCodec.Serialize(m)); }
        public void Dispose() { foreach (var node in Nodes) node.Dispose(); foreach (var key in Keys) key.Dispose(); }
    }
    private static void AssertAgreement(IEnumerable<AgreementNode> nodes, int expectedDecisions)
    {
        var decisions = nodes.Select(n => n.Decision).Where(d => d is not null).Select(d => d!).ToArray();
        Assert.Equal(expectedDecisions, decisions.Length);
        if (expectedDecisions == 0) return;
        Assert.Single(decisions.Select(d => d.Digest).Distinct());
        Assert.Single(decisions.Select(d => d.Estimate).Distinct());
        foreach (var d in decisions) { Assert.True(double.IsFinite(d.Estimate)); Assert.InRange(d.Estimate, 0, 1); }
    }
    [Fact] public void HonestNodesDecideLocallyWithPubliclyVerifiableCertificates()
    {
        using var s = new Swarm(); s.Begin(); s.Finish(); AssertAgreement(s.Nodes, 4);
        foreach (var node in s.Nodes)
        {
            Assert.Single(node.Log); var decision = node.Decision!;
            Assert.True(CommitCertificate.Verify(decision, s.Membership, s.Session));
            Assert.False(CommitCertificate.Verify(decision with { Estimate = 0.1 }, s.Membership, s.Session));
            Assert.False(CommitCertificate.Verify(decision, s.Membership, Guid.NewGuid()));
            Assert.False(CommitCertificate.Verify(decision with { CommitCertificate = Enumerable.Repeat(decision.CommitCertificate[0], 3).ToArray() }, s.Membership, s.Session));
            Assert.Equal(0.695, decision.Estimate, 10);
            Assert.True(decision.CommitCertificate.Count >= s.Membership.Quorum);
            using var verifier = new PublicKeyVerifier(node.Membership);
            Assert.All(decision.CommitCertificate, vote => {
                Assert.True(verifier.Verify(vote)); Assert.Equal(decision.Digest, vote.Digest);
                Assert.Equal(AgreementKind.Commit, vote.Kind); Assert.Equal(s.Session, vote.Session);
            });
            Assert.Equal(decision.CommitCertificate.Count, decision.CommitCertificate.Select(m => m.Sender).Distinct().Count());
        }
        Assert.NotSame(s.Nodes[0].Membership, s.Nodes[1].Membership);
        Assert.NotSame(s.Nodes[0].Log, s.Nodes[1].Log);
    }
    [Fact] public void FalseButAuthenticSensorReadingDoesNotBreakAgreement()
    {
        using var s = new Swarm();
        for (int i = 0; i < 4; i++) s.Nodes[i].BeginRound(1, i == 3 ? 1 : 0.7);
        s.Finish(); AssertAgreement(s.Nodes, 4);
        Assert.All(s.Nodes, n => Assert.Equal(0.7, n.Decision!.Estimate, 10));
    }
    [Fact] public void OneSilentPeerStillAllowsThreeHonestPeersToDecide()
    {
        using var s = new Swarm(); s.Silent.Add("R4");
        s.Network.Rule = p => new DeliveryRule(Drop: p.From == "R4" || p.To == "R4");
        s.Begin(); s.Finish(); AssertAgreement(s.Nodes.Take(3), 3); Assert.Null(s.Nodes[3].Decision);
    }
    [Fact] public void NetworkPartitionProducesNoDecisionRatherThanZero()
    {
        using var s = new Swarm();
        s.Network.Rule = p => new DeliveryRule(Drop: (p.From is "R1" or "R2") != (p.To is "R1" or "R2"));
        s.Begin(); s.Finish(); AssertAgreement(s.Nodes, 0); Assert.All(s.Nodes, n => Assert.Empty(n.Log));
    }
    [Fact] public void MissingProposerDoesNotManufactureDecisionOrReplaceLeader()
    {
        using var s = new Swarm(); s.Silent.Add("R1"); s.Begin(); s.Finish(); AssertAgreement(s.Nodes, 0);
    }
    [Fact] public void DuplicateAndReorderedDeliveryCannotInflateVotes()
    {
        using var s = new Swarm();
        s.Network.Rule = p => new DeliveryRule(DelayTicks: p.From == "R2" ? 2 : 1, Copies: 3);
        s.Begin(); s.Finish(); AssertAgreement(s.Nodes, 4);
        Assert.All(s.Nodes, n => Assert.True(n.Rejected > 0));
    }
    [Fact] public void TooFewDistinctVotersCannotFormQuorumEvenWithManyReplays()
    {
        using var s = new Swarm(); s.Begin(); s.Network.Run(2);
        s.Network.Rule = p => {
            var message = ProtocolCodec.Deserialize(p.Payload)!;
            return new DeliveryRule(Drop: (message.Kind is AgreementKind.Prepare or AgreementKind.Commit) && (p.From is "R3" or "R4"), Copies: 5);
        };
        s.Nodes[0].Propose(); s.Network.Run(12);
        Assert.Null(s.Nodes[0].Decision); Assert.Null(s.Nodes[1].Decision);
    }
    [Fact] public void ForgedCopyDoesNotConsumeTheAuthenticMessageIdOrSenderBudget()
    {
        using var s = new Swarm(); s.Begin();
        var reading = s.Reading(1, 0.72);
        var valid = s.Message(1, AgreementKind.Reading, reading: reading);
        var node = s.Nodes[0]; var accepted = node.Accepted;
        node.Receive(ProtocolCodec.Serialize(valid with { Signature = new string('0', 128) }));
        Assert.Equal(accepted, node.Accepted);
        node.Receive(ProtocolCodec.Serialize(valid)); Assert.Equal(accepted + 1, node.Accepted);
        node.Receive(ProtocolCodec.Serialize(valid)); Assert.Equal(accepted + 1, node.Accepted);
    }
    [Fact] public void IndependentKeyOwnerCannotImpersonateHonestPeer()
    {
        using var s = new Swarm(); var message = s.Message(0, AgreementKind.Prepare, new string('A', 64));
        Assert.Throws<InvalidOperationException>(() => s.Keys[1].Sign(message));
        using var publicOnly = new PublicKeyVerifier(s.Membership);
        Assert.False(publicOnly.Verify(message with { Sender = "R2" }));
    }
    [Fact] public void SignedUnadmittedSybilMessagesAreRejected()
    {
        using var s = new Swarm(); using var outsider = new NodeIdentity("S1"); s.Begin();
        var message = outsider.Sign(new AgreementMessage { Kind = AgreementKind.Prepare,
            Session = s.Session, Membership = s.Membership.Fingerprint, Round = 1, Sender = "S1", Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow, Digest = new string('A', 64) });
        foreach (var node in s.Nodes) { var count = node.Accepted; node.Receive(ProtocolCodec.Serialize(message)); Assert.Equal(count, node.Accepted); }
        s.Finish(); AssertAgreement(s.Nodes, 4);
    }
    [Fact] public void TamperingWithEveryEnvelopeFieldInvalidatesSignature()
    {
        using var s = new Swarm(); var m = s.Message(0, AgreementKind.Prepare, new string('A', 64));
        using var verifier = new PublicKeyVerifier(s.Membership);
        foreach (var bad in new[] { m with { Session = Guid.NewGuid() }, m with { Membership = new string('B', 64) },
            m with { Round = 2 }, m with { Sender = "R2" }, m with { Id = Guid.NewGuid() },
            m with { Timestamp = m.Timestamp.AddTicks(1) }, m with { Kind = AgreementKind.Commit },
            m with { Digest = new string('B', 64) }, m with { Readings = [s.Reading(0, 0.8)] },
            m with { Reading = s.Reading(0, 0.8) }, m with { Signature = new string('z', 128) } })
            Assert.False(verifier.Verify(bad));
    }
    [Fact] public void TamperingWithEveryReadingFieldInvalidatesSignature()
    {
        using var s = new Swarm(); var r = s.Reading(0, 0.7); using var verifier = new PublicKeyVerifier(s.Membership);
        foreach (var bad in new[] { r with { Session = Guid.NewGuid() }, r with { Membership = "changed" },
            r with { Round = 2 }, r with { Sender = "R2" }, r with { Id = Guid.NewGuid() },
            r with { Timestamp = r.Timestamp.AddTicks(1) }, r with { Value = 0.700000001 } }) Assert.False(verifier.Verify(bad));
    }
    [Fact] public void MalformedPacketsDoNotCrashOrConsumeAcceptedState()
    {
        using var s = new Swarm(); s.Begin(); var node = s.Nodes[0]; var accepted = node.Accepted;
        foreach (var packet in new[] { Array.Empty<byte>(), new byte[ProtocolCodec.MaxPacketBytes + 1],
            Encoding.UTF8.GetBytes("null"), Encoding.UTF8.GetBytes("{\"bad\":1}"), Encoding.UTF8.GetBytes("{\"Reading\":{\"Value\":NaN}}"),
            Encoding.UTF8.GetBytes(new string('[', 20) + new string(']', 20)) }) node.Receive(packet);
        var m = s.Message(1, AgreementKind.Prepare, new string('A', 64));
        foreach (var bad in new[] { m with { Readings = null! }, m with { Kind = (AgreementKind)99 },
            m with { Sender = null! }, m with { Id = Guid.Empty }, m with { Timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified) },
            m with { Timestamp = DateTime.UtcNow.AddHours(-1) }, m with { Timestamp = DateTime.UtcNow.AddHours(1) },
            m with { Digest = "bad" }, m with { Signature = null! }, m with { Readings = [null!] } })
            node.Receive(JsonSerializer.SerializeToUtf8Bytes(bad));
        Assert.Equal(accepted, node.Accepted); s.Finish(); AssertAgreement(s.Nodes, 4);
    }
    [Fact] public void SignedOutOfRangeReadingsAreRejected()
    {
        using var s = new Swarm(); s.Begin(); var node = s.Nodes[0]; var count = node.Accepted;
        foreach (var value in new[] { -1d, 2d }) node.Receive(ProtocolCodec.Serialize(s.Message(1, AgreementKind.Reading, reading: s.Reading(1, value))));
        Assert.Equal(count, node.Accepted);
        // Nonfinite JSON numbers cannot be emitted by the canonical signer or decoded from the wire.
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentException>(() => s.Reading(1, value));
    }
    [Fact] public void InvalidProposalDoesNotConsumeValidProposalLockOrMessageId()
    {
        using var s = new Swarm(); s.Begin();
        var vector = new[] { s.Reading(0, 0.7), s.Reading(1, 0.7), s.Reading(2, 0.7) };
        var m = s.Message(0, AgreementKind.Proposal, ProtocolCodec.Digest(vector), vector);
        var badVector = vector.Select((r, i) => i == 1 ? r with { Value = 0.8 } : r).ToArray();
        var bad = s.Keys[0].Sign(m with { Readings = badVector, Digest = ProtocolCodec.Digest(badVector) });
        var accepted = s.Nodes[1].Accepted; s.Nodes[1].Receive(ProtocolCodec.Serialize(bad)); Assert.Equal(accepted, s.Nodes[1].Accepted);
        s.Nodes[1].Receive(ProtocolCodec.Serialize(m)); Assert.True(s.Nodes[1].Accepted > accepted);
    }
    [Fact] public void DuplicateSendersAndInsufficientReadingsCannotEnterProposal()
    {
        using var s = new Swarm(); s.Begin();
        foreach (var vector in new[] { new[] { s.Reading(0, 0.7), s.Reading(0, 0.8), s.Reading(1, 0.7) }, new[] { s.Reading(0, 0.7) } })
        {
            var count = s.Nodes[1].Accepted;
            s.Nodes[1].Receive(ProtocolCodec.Serialize(s.Message(0, AgreementKind.Proposal, ProtocolCodec.Digest(vector), vector)));
            Assert.Equal(count, s.Nodes[1].Accepted);
        }
    }
    [Fact] public void EquivocatingProposerCannotMakeHonestPeersDecideConflictingDigests()
    {
        using var s = new Swarm(); s.Silent.Add("R1"); s.Begin(); s.Network.Run(2);
        var readings = Enumerable.Range(0, 4).Select(i => s.Reading(i, 0.7)).ToArray();
        var a = readings.Take(3).ToArray(); var b = readings.Where(r => r.Sender != "R3").ToArray();
        var da = ProtocolCodec.Digest(a); var db = ProtocolCodec.Digest(b);
        s.Inject(s.Message(0, AgreementKind.Proposal, da, a), 1);
        s.Inject(s.Message(0, AgreementKind.Proposal, db, b), 2, 3);
        // The Byzantine leader votes for both candidates, but honest nodes vote only for their lock.
        s.Inject(s.Message(0, AgreementKind.Prepare, da), 1);
        s.Inject(s.Message(0, AgreementKind.Commit, da), 1);
        s.Inject(s.Message(0, AgreementKind.Prepare, db), 2, 3);
        s.Inject(s.Message(0, AgreementKind.Commit, db), 2, 3);
        s.Network.Run(1);
        foreach (var digest in new[] { da, db })
        { s.Inject(s.Message(0, AgreementKind.Prepare, digest), 1, 2, 3); s.Inject(s.Message(0, AgreementKind.Commit, digest), 1, 2, 3); }
        s.Network.Run(12);
        Assert.Null(s.Nodes[1].Decision); AssertAgreement(s.Nodes.Skip(2), 2);
        Assert.All(s.Nodes.Skip(2), n => Assert.Equal(db, n.Decision!.Digest));
    }
    [Fact] public void CommitsWithoutLocalPrepareQuorumCannotCauseDecision()
    {
        using var s = new Swarm(); s.Begin();
        var vector = new[] { s.Reading(0, 0.7), s.Reading(1, 0.7), s.Reading(2, 0.7) }; var digest = ProtocolCodec.Digest(vector);
        s.Nodes[1].Receive(ProtocolCodec.Serialize(s.Message(0, AgreementKind.Proposal, digest, vector)));
        for (int i = 0; i < 4; i++) s.Nodes[1].Receive(ProtocolCodec.Serialize(s.Message(i, AgreementKind.Commit, digest)));
        Assert.Null(s.Nodes[1].Decision);
    }
    [Fact] public void NewRoundRotatesLeaderAndRejectsOldRoundAndSessionReplays()
    {
        using var s = new Swarm(); s.Begin(); s.Finish();
        var old = s.Message(0, AgreementKind.Prepare, s.Nodes[0].Decision!.Digest);
        foreach (var node in s.Nodes) node.CloseRound();
        s.Begin(2); Assert.Equal("R2", s.Membership.Leader(2));
        foreach (var node in s.Nodes)
        {
            var before = node.Accepted; node.Receive(ProtocolCodec.Serialize(old));
            var wrongSession = s.Keys[0].Sign(s.Message(0, AgreementKind.Prepare, new string('A', 64), round: 2) with { Session = Guid.NewGuid() });
            node.Receive(ProtocolCodec.Serialize(wrongSession)); Assert.Equal(before, node.Accepted);
        }
        s.Finish(); AssertAgreement(s.Nodes, 4);
        Assert.All(s.Nodes, n => { Assert.Equal(2, n.Log.Count); Assert.Equal(n.Log[0].LogHash, n.Log[1].PreviousHash); });
        Assert.Throws<InvalidOperationException>(() => s.Nodes[0].BeginRound(2, 0.7));
    }
    [Fact] public void PacketAndVerificationBudgetsRemainBoundedUnderFlooding()
    {
        using var s = new Swarm(maxIncoming: 16); s.Begin();
        foreach (var node in s.Nodes)
        {
            for (int i = 0; i < 1000; i++) node.Receive([0x7B]);
            Assert.Equal(16, node.Examined); Assert.True(node.LimitDrops > 0);
            Assert.InRange(node.SignatureChecks, 0, 2); Assert.Empty(node.Log);
        }
        s.Finish(); AssertAgreement(s.Nodes, 0);
        Assert.All(s.Nodes, n => Assert.Equal(16, n.Examined));
    }
    [Fact] public void TransportQueueAndByteLimitsBoundPendingWork()
    {
        var network = new SimulatedNetwork(capacity: 8); int delivered = 0; network.Register("R1", _ => delivered++);
        for (int i = 0; i < 1000; i++) network.Send("attacker", "R1", [1]);
        network.Send("attacker", "R1", new byte[ProtocolCodec.MaxPacketBytes + 1]);
        Assert.Equal(8, network.Pending); Assert.Equal(8, network.MaxQueued); Assert.Equal(993, network.Dropped);
        network.Run(1); Assert.Equal(8, delivered);
    }
    [Fact] public void MembershipRequiresSafeQuorumAndRejectsSharedKeys()
    {
        using var key = new NodeIdentity("R1");
        Assert.Throws<ArgumentException>(() => new SwarmMembership(new[] { KeyValuePair.Create("R1", key.PublicKey) }, 1));
        Assert.Throws<ArgumentException>(() => new SwarmMembership(new[] { KeyValuePair.Create("R1", key.PublicKey), KeyValuePair.Create("R2", key.PublicKey) }, 0));
    }
    [Fact] public void CanonicalSigningAndLocalDecisionIgnoreCurrentCulture()
    {
        using var s = new Swarm(); var old = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); s.Begin(); s.Finish(); AssertAgreement(s.Nodes, 4); }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void EquivocationAndReorderingPreserveSafetyAcrossDifferentHonestLocks(int locks)
    {
        using var s = new Swarm(); s.Silent.Add("R1"); s.Begin(); s.Network.Run(2);
        var random = new Random(locks);
        s.Network.Rule = _ => new DeliveryRule(DelayTicks: random.Next(1, 5), Copies: random.Next(1, 4));
        var readings = Enumerable.Range(0, 4).Select(i => s.Reading(i, 0.68 + i * 0.01)).ToArray();
        var vectors = new[] { readings.Take(3).ToArray(), readings.Where(r => r.Sender != "R3").ToArray() };
        for (int i = 1; i < 4; i++)
        {
            var v = vectors[(locks >> (i - 1)) & 1];
            s.Inject(s.Message(0, AgreementKind.Proposal, ProtocolCodec.Digest(v), v), i);
        }
        foreach (var vector in vectors)
        {
            var digest = ProtocolCodec.Digest(vector);
            s.Inject(s.Message(0, AgreementKind.Prepare, digest), 1, 2, 3);
            s.Inject(s.Message(0, AgreementKind.Commit, digest), 1, 2, 3);
        }
        s.Network.Run(30);
        var decisions = s.Nodes.Skip(1).Where(n => n.Decision is not null).Select(n => n.Decision!).ToArray();
        Assert.True(decisions.Select(d => d.Digest).Distinct().Count() <= 1);
        Assert.All(decisions, d => Assert.True(CommitCertificate.Verify(d, s.Membership, s.Session)));
    }

    [Fact] public void SevenNodesWithTwoSilentPeersStillDecideUsingFiveDistinctVotes()
    {
        using var s = new Swarm(n: 7, f: 2); s.Silent.UnionWith(new[] { "R6", "R7" });
        s.Network.Rule = p => new DeliveryRule(Drop: s.Silent.Contains(p.From) || s.Silent.Contains(p.To));
        s.Begin(); s.Finish(); AssertAgreement(s.Nodes.Take(5), 5);
        Assert.All(s.Nodes.Take(5), n => Assert.True(n.Decision!.CommitCertificate.Count >= 5));
    }
}

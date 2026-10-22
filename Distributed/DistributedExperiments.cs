using System.Globalization;

namespace SwarmSecureCoordination.Distributed;

public static class DistributedExperiments
{
    public static void Run()
    {
        foreach (var scenario in new[] { "Honest swarm", "Faulty sensor", "Sybil and replay", "Silent peer", "Partition", "Equivocating proposer", "Flood" })
            RunScenario(scenario);
    }
    private static void RunScenario(string scenario)
    {
        var identities = Enumerable.Range(1, 4).Select(i => new NodeIdentity($"R{i}")).ToArray();
        var nodes = new List<AgreementNode>();
        try
        {
            var membership = new SwarmMembership(identities.Select(i => KeyValuePair.Create(i.Id, i.PublicKey)), 1);
            var session = Guid.NewGuid();
            var network = new SimulatedNetwork();
            var faultyLeader = scenario == "Equivocating proposer";
            foreach (var identity in identities)
            {
                var node = new AgreementNode(identity, membership, session, network, maxIncomingPerRound: scenario == "Flood" ? 32 : 4096);
                nodes.Add(node);
                network.Register(node.Id, bytes => { if (!(faultyLeader && node.Id == "R1")) node.Receive(bytes); });
            }
            var observed = new Dictionary<string, SignedReading>(StringComparer.Ordinal);
            network.Rule = packet => {
                var message = ProtocolCodec.Deserialize(packet.Payload);
                if (message?.Kind == AgreementKind.Reading && message.Reading is not null && (packet.To == "R1" || packet.From == "R1"))
                    observed.TryAdd(message.Sender, message.Reading);
                return new DeliveryRule(Drop: scenario == "Partition" && ((packet.From is "R1" or "R2") != (packet.To is "R1" or "R2")) ||
                    scenario == "Silent peer" && (packet.From == "R4" || packet.To == "R4"));
            };
            for (int i = 0; i < nodes.Count; i++) nodes[i].BeginRound(1, scenario == "Faulty sensor" && i == 3 ? 1 : 0.68 + i * 0.01);
            if (scenario == "Sybil and replay")
            {
                using var outsider = new NodeIdentity("S1");
                var fake = outsider.Sign(new SignedReading { Session = session, Membership = membership.Fingerprint,
                    Round = 1, Sender = outsider.Id, Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Value = 1 });
                var message = outsider.Sign(new AgreementMessage { Kind = AgreementKind.Reading, Session = session,
                    Membership = membership.Fingerprint, Round = 1, Sender = outsider.Id, Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Reading = fake });
                foreach (var node in nodes) network.Send("S1", node.Id, ProtocolCodec.Serialize(message));
                network.Rule = _ => new DeliveryRule(Copies: 3); // Duplicate proposals/votes must not inflate quorums.
            }
            if (scenario == "Flood")
                foreach (var node in nodes) for (int i = 0; i < 100; i++) network.Send("attacker", node.Id, [0x7B]);
            network.Run(2);
            if (faultyLeader)
            {
                // Byzantine leader owns only R1's key. It can omit readings and sign two different proposals.
                var readings = observed.Values.OrderBy(r => r.Sender, StringComparer.Ordinal).ToArray();
                var a = readings.Take(3).ToArray(); var b = readings.Where(r => r.Sender != "R3").ToArray();
                foreach (var peer in nodes.Skip(1))
                {
                    var vector = peer.Id == "R2" ? a : b;
                    var proposal = identities[0].Sign(new AgreementMessage { Kind = AgreementKind.Proposal, Session = session,
                        Membership = membership.Fingerprint, Round = 1, Sender = "R1", Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow,
                        Readings = vector, Digest = ProtocolCodec.Digest(vector) });
                    network.Send("R1", peer.Id, ProtocolCodec.Serialize(proposal));
                }
            }
            else foreach (var node in nodes) node.Propose();
            network.Run(12);
            foreach (var node in nodes) node.CloseRound();
            var honest = nodes.Where(n => !(faultyLeader && n.Id == "R1") && !(scenario == "Silent peer" && n.Id == "R4")).ToArray();
            var decisions = honest.Where(n => n.Decision is not null).Select(n => n.Decision!).ToArray();
            Console.WriteLine($"\n{scenario} (n=4, f=1, quorum=3):");
            foreach (var node in honest)
                Console.WriteLine($"  {node.Id}: {(node.Decision is null ? "no decision" : node.Decision.Estimate.ToString("F4", CultureInfo.InvariantCulture))}; rejected={node.Rejected}, limit drops={node.LimitDrops}");
            Console.WriteLine($"  Decided={decisions.Length}/{honest.Length}; conflicting decisions={decisions.Select(d => d.Digest).Distinct().Count() > 1}; delivered={network.Delivered}, network drops={network.Dropped}");
            if (decisions.Length > 0) Console.WriteLine($"  Absolute error from reference 0.7000={Math.Abs(decisions[0].Estimate - 0.7):F4}");
        }
        finally { foreach (var node in nodes) node.Dispose(); foreach (var identity in identities) identity.Dispose(); }
    }
}

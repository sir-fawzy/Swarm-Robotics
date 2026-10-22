using System.Collections.ObjectModel;
using System.Text;
using SwarmSecureCoordination.Core;
using SwarmSecureCoordination.Interfaces;

namespace SwarmSecureCoordination.Distributed;

// One single-threaded robot endpoint. The network owns delivery scheduling, never decisions.
// One proposal lock per (session, membership, round), with no retry/view change inside that round.
public sealed class AgreementNode : IDisposable
{
    private readonly INodeSigner _signer;
    private readonly IPublicKeyVerifier _verifier;
    private readonly IRobotNetwork _network;
    private readonly TimeProvider _clock;
    private readonly IFilterModule _filter = new FilterModule();
    private readonly IConsensusModule _consensus = new ConsensusModule();
    private readonly int _maxIncoming;
    private readonly Dictionary<string, SignedReading> _readings = new(StringComparer.Ordinal);
    private readonly Dictionary<(AgreementKind, string), AgreementMessage> _votes = new();
    private readonly HashSet<(string, AgreementKind, Guid)> _seen = new();
    private readonly List<LocalDecision> _log = [];
    private AgreementMessage? _proposal;
    private bool _commitSent;
    private bool _proposalSent;
    private bool _closed = true;
    public string Id => _signer.Id;
    public Guid Session { get; }
    public SwarmMembership Membership { get; }
    public int Round { get; private set; }
    public int Examined { get; private set; }
    public int Rejected { get; private set; }
    public int Accepted { get; private set; }
    public int SignatureChecks { get; private set; }
    public int LimitDrops { get; private set; }
    public LocalDecision? Decision { get; private set; }
    public IReadOnlyList<LocalDecision> Log => _log.AsReadOnly();

    public AgreementNode(INodeSigner signer, SwarmMembership membership, Guid session, IRobotNetwork network,
        TimeProvider? clock = null, int maxIncomingPerRound = 4096)
    {
        if (session == Guid.Empty || maxIncomingPerRound < 1 || !membership.Contains(signer.Id) ||
            !membership.GetPublicKey(signer.Id).SequenceEqual(signer.PublicKey)) throw new ArgumentException("Invalid node configuration");
        _signer = signer; Membership = membership.Copy(); Session = session; _network = network;
        _clock = clock ?? TimeProvider.System; _maxIncoming = maxIncomingPerRound;
        _verifier = new PublicKeyVerifier(Membership);
    }
    public void BeginRound(int round, double value)
    {
        if (!_closed || round <= Round) throw new InvalidOperationException("Close the previous round; round numbers must increase");
        if (!double.IsFinite(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(value));
        Round = round; _closed = false; Examined = Rejected = Accepted = SignatureChecks = LimitDrops = 0;
        _readings.Clear(); _votes.Clear(); _seen.Clear(); _proposal = null; Decision = null;
        _commitSent = _proposalSent = false;
        var reading = _signer.Sign(new SignedReading { Session = Session, Membership = Membership.Fingerprint,
            Round = Round, Sender = Id, Id = Guid.NewGuid(), Timestamp = Now, Value = value });
        Emit(NewMessage(AgreementKind.Reading) with { Reading = reading });
    }
    public void CloseRound() => _closed = true;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private AgreementMessage NewMessage(AgreementKind kind) => new() { Kind = kind, Session = Session,
        Membership = Membership.Fingerprint, Round = Round, Sender = Id, Id = Guid.NewGuid(), Timestamp = Now };
    private void Emit(AgreementMessage message)
    {
        var bytes = ProtocolCodec.Serialize(_signer.Sign(message));
        Receive(bytes); // Own vote is local; it does not depend on transport loopback.
        _network.Broadcast(Id, bytes);
    }
    // Called at the reading-collection deadline. Only the rotating proposer acts.
    public bool Propose()
    {
        if (_closed || Id != Membership.Leader(Round) || _proposalSent || _readings.Count < Membership.Quorum) return false;
        _proposalSent = true;
        var readings = _readings.Values.OrderBy(r => r.Sender, StringComparer.Ordinal).ToArray();
        Emit(NewMessage(AgreementKind.Proposal) with { Readings = readings, Digest = ProtocolCodec.Digest(readings) });
        return true;
    }
    private bool Header(Guid session, string membership, int round, string sender, Guid id, DateTime timestamp)
    {
        if (session != Session || membership != Membership.Fingerprint || round != Round || !Membership.Contains(sender) ||
            id == Guid.Empty || timestamp.Kind != DateTimeKind.Utc) return false;
        var age = Now - timestamp;
        return age <= TimeSpan.FromMinutes(5) && age >= -TimeSpan.FromSeconds(30);
    }
    private bool ValidReading(SignedReading? r)
    {
        if (r is null || !Header(r.Session, r.Membership, r.Round, r.Sender, r.Id, r.Timestamp) ||
            !double.IsFinite(r.Value) || r.Value < 0 || r.Value > 1 || r.Signature is not { Length: 128 }) return false;
        SignatureChecks++;
        return _verifier.Verify(r);
    }
    private bool Shape(AgreementMessage m)
    {
        if (!Enum.IsDefined(m.Kind) || m.Readings is null || m.Readings.Length > Membership.Ids.Count ||
            m.Readings.Any(r => r is null) || m.Signature is not { Length: 128 }) return false;
        return m.Kind switch {
            AgreementKind.Reading => m.Reading is not null && m.Readings.Length == 0 && m.Digest == "",
            AgreementKind.Proposal => m.Reading is null && m.Sender == Membership.Leader(Round) &&
                m.Readings.Length >= Membership.Quorum && ProtocolCodec.IsDigest(m.Digest),
            _ => m.Reading is null && m.Readings.Length == 0 && ProtocolCodec.IsDigest(m.Digest)
        };
    }
    public void Receive(byte[] packet)
    {
        if (_closed) return;
        if (Examined >= _maxIncoming) { LimitDrops++; return; }
        Examined++;
        var m = ProtocolCodec.Deserialize(packet);
        if (m is null || !Header(m.Session, m.Membership, m.Round, m.Sender, m.Id, m.Timestamp) || !Shape(m)) { Rejected++; return; }
        // Numeric fields must be finite before canonical serialization, even in malformed payloads.
        if ((m.Reading is not null && !double.IsFinite(m.Reading.Value)) || m.Readings.Any(r => !double.IsFinite(r.Value))) { Rejected++; return; }
        SignatureChecks++;
        if (!_verifier.Verify(m)) { Rejected++; return; }
        if (m.Kind == AgreementKind.Reading && (m.Reading!.Sender != m.Sender || !ValidReading(m.Reading))) { Rejected++; return; }
        if (m.Kind == AgreementKind.Proposal)
        {
            var names = m.Readings.Select(r => r.Sender).ToArray();
            if (!names.SequenceEqual(names.Order(StringComparer.Ordinal)) || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                m.Readings.Any(r => !ValidReading(r)) || m.Digest != ProtocolCodec.Digest(m.Readings)) { Rejected++; return; }
        }
        // Replay and per-kind budgets are consumed only after full authentication/validation.
        // One reading and one vote of each kind per admitted sender per round.
        if (_seen.Contains((m.Sender, m.Kind, m.Id))) { Rejected++; return; }
        if (m.Kind == AgreementKind.Reading)
        {
            if (!_readings.TryAdd(m.Sender, m.Reading!)) { Rejected++; return; }
        }
        else if (m.Kind == AgreementKind.Proposal)
        {
            if (_proposal is not null) { Rejected++; return; }
            _proposal = m;
        }
        else if (!_votes.TryAdd((m.Kind, m.Sender), m)) { Rejected++; return; }
        _seen.Add((m.Sender, m.Kind, m.Id)); Accepted++;
        if (m.Kind == AgreementKind.Proposal) Emit(NewMessage(AgreementKind.Prepare) with { Digest = m.Digest });
        Progress();
    }
    private int Votes(AgreementKind kind) => _votes.Values.Count(m => m.Kind == kind && m.Digest == _proposal!.Digest);
    private void Progress()
    {
        if (_proposal is null || Votes(AgreementKind.Prepare) < Membership.Quorum) return;
        if (!_commitSent)
        {
            _commitSent = true;
            Emit(NewMessage(AgreementKind.Commit) with { Digest = _proposal.Digest });
        }
        if (Decision is not null || Votes(AgreementKind.Commit) < Membership.Quorum) return;
        var estimate = _consensus.Compute(_filter.Filter(_proposal.Readings.Select(r => r.Value).ToList()));
        if (estimate is null || !double.IsFinite(estimate.Value)) throw new InvalidOperationException("Agreement requires a finite deterministic estimate");
        var previous = _log.Count == 0 ? "GENESIS" : _log[^1].LogHash;
        var hash = ProtocolCodec.Hash(Encoding.UTF8.GetBytes($"swarm-local-log/v1|{previous}|{_proposal.Digest}"));
        var certificate = _votes.Values.Where(m => m.Kind == AgreementKind.Commit && m.Digest == _proposal.Digest)
            .OrderBy(m => m.Sender, StringComparer.Ordinal).ToArray();
        Decision = new LocalDecision(Round, _proposal.Digest, estimate.Value,
            new ReadOnlyCollection<SignedReading>(_proposal.Readings.ToArray()),
            new ReadOnlyCollection<AgreementMessage>(certificate), previous, hash);
        _log.Add(Decision);
    }
    public void Dispose() => _verifier.Dispose(); // The credential owner controls the private key lifetime.
}

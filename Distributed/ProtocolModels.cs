namespace SwarmSecureCoordination.Distributed;

public enum AgreementKind { Reading, Proposal, Prepare, Commit }

public sealed record SignedReading
{
    public Guid Session { get; init; }
    public string Membership { get; init; } = "";
    public int Round { get; init; }
    public string Sender { get; init; } = "";
    public Guid Id { get; init; }
    public DateTime Timestamp { get; init; }
    public double Value { get; init; }
    public string Signature { get; init; } = "";
}

public sealed record AgreementMessage
{
    public AgreementKind Kind { get; init; }
    public Guid Session { get; init; }
    public string Membership { get; init; } = "";
    public int Round { get; init; }
    public string Sender { get; init; } = "";
    public Guid Id { get; init; }
    public DateTime Timestamp { get; init; }
    public string Digest { get; init; } = "";
    public SignedReading? Reading { get; init; }
    public SignedReading[] Readings { get; init; } = [];
    public string Signature { get; init; } = "";
}

// A local decision and its independently verifiable commit evidence. No shared decision store.
public sealed record LocalDecision(int Round, string Digest, double Estimate,
    IReadOnlyList<SignedReading> Readings, IReadOnlyList<AgreementMessage> CommitCertificate,
    string PreviousHash, string LogHash);

public interface INodeSigner : IDisposable
{
    string Id { get; }
    byte[] PublicKey { get; }
    SignedReading Sign(SignedReading reading);
    AgreementMessage Sign(AgreementMessage message);
}

public interface IPublicKeyVerifier : IDisposable
{
    bool Verify(SignedReading reading);
    bool Verify(AgreementMessage message);
}

public interface IRobotNetwork
{
    void Broadcast(string sender, byte[] payload);
}

namespace SwarmSecureCoordination.Models;
public sealed record Message
{
    public string RobotId { get; init; } = "";
    public int RoundId { get; init; }
    public double Value { get; init; }
    public Guid MessageId { get; init; }
    public DateTime Timestamp { get; init; }
    public string Signature { get; init; } = "";
    // Sender's observed log snapshot; authenticated context, not a freshness proof.
    public string PrevHash { get; init; } = "GENESIS";
}

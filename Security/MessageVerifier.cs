using SwarmSecureCoordination.Interfaces;
using SwarmSecureCoordination.Models;
namespace SwarmSecureCoordination.Security;
public sealed class MessageVerifier(IIdentityVerifier identities, TimeProvider? clock = null) : IMessageVerifier
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public bool Verify(Message message, int expectedRound)
    {
        if (message is null || !IdentityManager.ValidId(message.RobotId) ||
            expectedRound < 1 || message.RoundId != expectedRound || message.MessageId == Guid.Empty ||
            message.Timestamp.Kind != DateTimeKind.Utc || !double.IsFinite(message.Value) ||
            message.Value < 0 || message.Value > 1 || message.Signature is not { Length: 64 } ||
            message.PrevHash is null || !(message.PrevHash == "GENESIS" ||
            (message.PrevHash.Length == 64 && message.PrevHash.All(char.IsAsciiHexDigit)))) return false;
        var age = _clock.GetUtcNow().UtcDateTime - message.Timestamp;
        if (age > TimeSpan.FromMinutes(5) || age < -TimeSpan.FromSeconds(30)) return false;
        return identities.IsAdmitted(message.RobotId) && identities.VerifySignature(message);
    }
}

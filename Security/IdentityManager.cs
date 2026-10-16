using System.Security.Cryptography;
using SwarmSecureCoordination.Models;
using SwarmSecureCoordination.Interfaces;
namespace SwarmSecureCoordination.Security;
// Trusted simulation provisioning authority. Never pass this object to robots or attackers.
public sealed class IdentityManager : IIdentityVerifier
{
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _admitted = new(StringComparer.Ordinal);
    public IMessageSigner Provision(string robotId, bool admitted)
    {
        if (!ValidId(robotId)) throw new ArgumentException("Invalid identity");
        var key = RandomNumberGenerator.GetBytes(32);
        _keys.Add(robotId, key);
        if (admitted) _admitted.Add(robotId);
        return new Signer(robotId, (byte[])key.Clone());
    }
    public static bool ValidId(string? id) => id is { Length: > 0 and <= 64 } &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    public bool IsAdmitted(string robotId) => ValidId(robotId) && _admitted.Contains(robotId);
    public bool VerifySignature(Message message)
    {
        if (!_keys.TryGetValue(message.RobotId, out var key) || message.Signature is not { Length: 64 }) return false;
        try { return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, AuthenticatedPayload.Encode(message)), Convert.FromHexString(message.Signature)); }
        catch (FormatException) { return false; }
    }
    private sealed class Signer(string robotId, byte[] key) : IMessageSigner
    {
        public string RobotId => robotId;
        public string Sign(Message message)
        {
            if (message.RobotId != robotId) throw new InvalidOperationException("Signer cannot impersonate another robot");
            return Convert.ToHexString(HMACSHA256.HashData(key, AuthenticatedPayload.Encode(message)));
        }
    }
}

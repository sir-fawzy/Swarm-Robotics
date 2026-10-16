using SwarmSecureCoordination.Interfaces;
using SwarmSecureCoordination.Models;
namespace SwarmSecureCoordination.Core;
public class Robot(IMessageSigner signer, double trueValue)
{
    public string RobotId => signer.RobotId;
    public double TrueValue => trueValue;
    public virtual double Sense() => Math.Clamp(TrueValue + Random.Shared.NextDouble() * 0.1 - 0.05, 0, 1);
    public Message CreateMessage(int roundId, string prevHash)
    {
        var message = new Message { RobotId = RobotId, RoundId = roundId, Value = Sense(),
            MessageId = Guid.NewGuid(), Timestamp = DateTime.UtcNow, PrevHash = prevHash };
        return message with { Signature = signer.Sign(message) };
    }
}

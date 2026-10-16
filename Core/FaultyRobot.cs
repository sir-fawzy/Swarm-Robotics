using SwarmSecureCoordination.Interfaces;
namespace SwarmSecureCoordination.Core;
public sealed class FaultyRobot(IMessageSigner signer, double trueValue, double maliciousValue) : Robot(signer, trueValue)
{ public override double Sense() => maliciousValue; }

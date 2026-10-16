using SwarmSecureCoordination.Models;
namespace SwarmSecureCoordination.Interfaces;
public interface IMessageSigner { string RobotId { get; } string Sign(Message message); }
public interface IIdentityVerifier { bool IsAdmitted(string robotId); bool VerifySignature(Message message); }

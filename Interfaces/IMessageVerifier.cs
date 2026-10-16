using SwarmSecureCoordination.Models;

namespace SwarmSecureCoordination.Interfaces;

public interface IMessageVerifier
{
    bool Verify(Message message, int expectedRound);
}
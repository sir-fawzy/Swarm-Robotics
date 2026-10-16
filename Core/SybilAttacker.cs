using SwarmSecureCoordination.Interfaces;
using SwarmSecureCoordination.Models;
namespace SwarmSecureCoordination.Core;
// Receives only attacker-owned capabilities, never the trusted provisioning authority.
public sealed class SybilAttacker(IEnumerable<IMessageSigner> fakeSigners, double maliciousValue)
{
    public List<Message> CreateSybilMessages(int roundId, string prevHash) => fakeSigners
        .Select(s => new FaultyRobot(s, maliciousValue, maliciousValue).CreateMessage(roundId, prevHash)).ToList();
}

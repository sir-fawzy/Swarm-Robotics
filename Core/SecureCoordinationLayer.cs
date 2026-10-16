using SwarmSecureCoordination.Interfaces;
using SwarmSecureCoordination.Models;
namespace SwarmSecureCoordination.Core;
// Single centralized aggregator; callers serialize round initialization with Process.
public sealed class SecureCoordinationLayer(
    IMessageVerifier verifier, IBudgetManager budget, IHashLog log,
    IFilterModule filter, IConsensusModule consensus, int maxIncomingPerRound = 1024)
{
    private int _round;
    private int _examined;
    private readonly HashSet<(string, Guid)> _accepted = new();
    private readonly object _gate = new();
    public double? Process(IEnumerable<Message> messages, int round)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (round < 1 || maxIncomingPerRound < 1) throw new ArgumentOutOfRangeException(nameof(round));
        lock (_gate)
        {
            if (round < _round) return null;
            if (round > _round) { _round = round; _examined = 0; _accepted.Clear(); }
            var values = new List<double>();
            // Take bounds enumeration too: no extra MoveNext after the limit.
            foreach (var message in messages.Take(maxIncomingPerRound - _examined))
            {
                _examined++;
                if (!verifier.Verify(message, round)) continue;
                var key = (message.RobotId, message.MessageId);
                if (_accepted.Contains(key) || !budget.Consume(message.RobotId, round)) continue;
                log.Append(message);
                _accepted.Add(key); values.Add(message.Value);
            }
            return consensus.Compute(filter.Filter(values));
        }
    }
}

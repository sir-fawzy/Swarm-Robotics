using SwarmSecureCoordination.Interfaces;
namespace SwarmSecureCoordination.Security;
public sealed class BudgetManager : IBudgetManager
{
    private readonly int _defaultBudget;
    private int _round;
    private Dictionary<string, int> _remaining = new(StringComparer.Ordinal);
    public BudgetManager(int defaultBudget = 1)
    { if (defaultBudget < 1) throw new ArgumentOutOfRangeException(nameof(defaultBudget)); _defaultBudget = defaultBudget; }
    public void InitializeRound(int roundId, List<string> robotIds)
    {
        if (roundId <= _round) throw new ArgumentOutOfRangeException(nameof(roundId), "Rounds must increase; budgets cannot be reset");
        ArgumentNullException.ThrowIfNull(robotIds);
        if (robotIds.Any(id => !IdentityManager.ValidId(id))) throw new ArgumentException("Invalid identity");
        var next = robotIds.Distinct(StringComparer.Ordinal).ToDictionary(id => id, _ => _defaultBudget, StringComparer.Ordinal);
        _remaining = next; _round = roundId;
    }
    public bool Consume(string robotId, int roundId)
    {
        if (robotId is null || roundId != _round || !_remaining.TryGetValue(robotId, out var left) || left <= 0) return false;
        _remaining[robotId] = left - 1; return true;
    }
}

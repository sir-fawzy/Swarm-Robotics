namespace SwarmSecureCoordination.Interfaces;

public interface IBudgetManager
{
    void InitializeRound(int roundId, List<string> robotIds);
    bool Consume(string robotId, int roundId);
}
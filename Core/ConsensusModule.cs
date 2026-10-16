using SwarmSecureCoordination.Interfaces;

namespace SwarmSecureCoordination.Core;

public class ConsensusModule : IConsensusModule
{
    public double? Compute(List<double> values)
    {
        if (values is null || !values.Any()) return null;
        return values.Average();
    }
}
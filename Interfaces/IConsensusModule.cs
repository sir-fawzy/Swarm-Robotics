namespace SwarmSecureCoordination.Interfaces;

public interface IConsensusModule
{
    double? Compute(List<double> values);
}
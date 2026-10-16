namespace SwarmSecureCoordination.Core;

public class BaselineConsensus
{
    public double? Compute(List<double> values)
    {
        if (values == null || values.Count == 0)
            return null;

        return values.Average();
    }
}
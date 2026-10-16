using SwarmSecureCoordination.Interfaces;

namespace SwarmSecureCoordination.Core;

public class FilterModule : IFilterModule
{
    public List<double> Filter(List<double> values)
    {
        if (values.Count <= 2) return values;

        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Skip(1).Take(sorted.Count - 2).ToList();
    }
}
namespace SwarmSecureCoordination.Interfaces;

public interface IFilterModule
{
    List<double> Filter(List<double> values);
}
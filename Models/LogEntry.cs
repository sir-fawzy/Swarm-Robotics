namespace SwarmSecureCoordination.Models;

public class LogEntry
{
    public string RobotId { get; set; } = "";
    public int RoundId { get; set; }
    public double Value { get; set; }
    public string PrevHash { get; set; } = "";
    public string CurrentHash { get; set; } = "";
}
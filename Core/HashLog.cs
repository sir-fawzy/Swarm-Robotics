using System.Security.Cryptography;
using System.Text;
using SwarmSecureCoordination.Interfaces;
using SwarmSecureCoordination.Models;

namespace SwarmSecureCoordination.Core;

public class HashLog : IHashLog
{
    private readonly List<LogEntry> _entries = new();

    public string GetLastHash()
    {
        return _entries.Count == 0 ? "GENESIS" : _entries.Last().CurrentHash;
    }

    public void Append(Message message)
    {
        var prevHash = GetLastHash();
        var raw = System.Text.Json.JsonSerializer.Serialize(new[] { prevHash, Convert.ToBase64String(SwarmSecureCoordination.Security.AuthenticatedPayload.Encode(message)), message.Signature });
        var currentHash = Hash(raw);

        _entries.Add(new LogEntry
        {
            RobotId = message.RobotId,
            RoundId = message.RoundId,
            Value = message.Value,
            PrevHash = prevHash,
            CurrentHash = currentHash
        });
    }

    private static string Hash(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
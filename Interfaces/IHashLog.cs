using SwarmSecureCoordination.Models;

namespace SwarmSecureCoordination.Interfaces;

public interface IHashLog
{
    string GetLastHash();
    void Append(Message message);
}
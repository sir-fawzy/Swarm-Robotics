using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using SwarmSecureCoordination.Security;

namespace SwarmSecureCoordination.Distributed;

// A fixed, trusted roster. Generating a key does not admit its owner.
public sealed class SwarmMembership
{
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Ids { get; }
    public int FaultLimit { get; }
    public int Quorum => Ids.Count - FaultLimit;
    public string Fingerprint { get; }
    public SwarmMembership(IEnumerable<KeyValuePair<string, byte[]>> publicKeys, int faultLimit)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        foreach (var pair in publicKeys)
        {
            if (!IdentityManager.ValidId(pair.Key) || pair.Value is null) throw new ArgumentException("Invalid member");
            using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(pair.Value, out var consumed);
            if (consumed != pair.Value.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new ArgumentException("Only P-256 public keys are supported");
            _keys.Add(pair.Key, key.ExportSubjectPublicKeyInfo());
            if (_keys.Count > 32) throw new ArgumentException("Simulation supports at most 32 nodes");
        }
        if (faultLimit < 0 || _keys.Count == 0 || (long)_keys.Count < 3L * faultLimit + 1)
            throw new ArgumentException("Membership requires n >= 3f + 1");
        if (_keys.Values.Select(Convert.ToHexString).Distinct(StringComparer.Ordinal).Count() != _keys.Count)
            throw new ArgumentException("Different identities must have different public keys");
        Ids = new ReadOnlyCollection<string>(_keys.Keys.Order(StringComparer.Ordinal).ToArray());
        FaultLimit = faultLimit;
        Fingerprint = ProtocolCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(new {
            Protocol = "swarm-membership/v1", FaultLimit,
            Members = Ids.Select(id => new { Id = id, PublicKey = Convert.ToHexString(_keys[id]) }).ToArray()
        }));
    }
    public bool Contains(string? id) => id is not null && _keys.ContainsKey(id);
    public byte[] GetPublicKey(string id) => (byte[])_keys[id].Clone();
    public string Leader(int round)
    {
        if (round < 1) throw new ArgumentOutOfRangeException(nameof(round));
        return Ids[(round - 1) % Ids.Count];
    }
    public SwarmMembership Copy() => new(Ids.Select(id => KeyValuePair.Create(id, GetPublicKey(id))), FaultLimit);
}

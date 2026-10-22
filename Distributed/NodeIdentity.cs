using System.Security.Cryptography;
using SwarmSecureCoordination.Security;

namespace SwarmSecureCoordination.Distributed;

// Created by trusted setup, then handed exclusively to its owner (or the modeled attacker).
public sealed class NodeIdentity : INodeSigner
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string Id { get; }
    public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();
    public NodeIdentity(string id)
    {
        if (!IdentityManager.ValidId(id)) throw new ArgumentException("Invalid node identity", nameof(id));
        Id = id;
    }
    private string Sign(string sender, byte[] payload)
    {
        if (sender != Id) throw new InvalidOperationException("Cannot sign for another identity");
        return Convert.ToHexString(_key.SignData(payload, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
    public SignedReading Sign(SignedReading r) => r with { Signature = Sign(r.Sender, ProtocolCodec.Unsigned(r)) };
    public AgreementMessage Sign(AgreementMessage m) => m with { Signature = Sign(m.Sender, ProtocolCodec.Unsigned(m)) };
    public void Dispose() => _key.Dispose();
}

// Each node imports its own verifier instances. These objects contain public keys only.
public sealed class PublicKeyVerifier : IPublicKeyVerifier
{
    private readonly Dictionary<string, ECDsa> _keys = new(StringComparer.Ordinal);
    public PublicKeyVerifier(SwarmMembership membership)
    {
        foreach (var id in membership.Ids)
        {
            var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(membership.GetPublicKey(id), out _);
            _keys.Add(id, key);
        }
    }
    private bool Verify(string id, string signature, byte[] payload)
    {
        if (signature is not { Length: 128 } || !_keys.TryGetValue(id, out var key)) return false;
        try { return key.VerifyData(payload, Convert.FromHexString(signature), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation); }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }
    public bool Verify(SignedReading r) => Verify(r.Sender, r.Signature, ProtocolCodec.Unsigned(r));
    public bool Verify(AgreementMessage m) => Verify(m.Sender, m.Signature, ProtocolCodec.Unsigned(m));
    public void Dispose() { foreach (var key in _keys.Values) key.Dispose(); }
}

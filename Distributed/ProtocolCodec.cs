using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwarmSecureCoordination.Distributed;

public static class ProtocolCodec
{
    public const int MaxPacketBytes = 65536;
    private static readonly JsonSerializerOptions WireOptions = new()
    { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static byte[] Serialize(AgreementMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, WireOptions);
    public static AgreementMessage? Deserialize(byte[] packet)
    {
        if (packet.Length == 0 || packet.Length > MaxPacketBytes) return null;
        try { return JsonSerializer.Deserialize<AgreementMessage>(packet, WireOptions); }
        catch (JsonException) { return null; }
    }
    private static byte[] Encode(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) { write(writer); }
        return stream.ToArray();
    }
    private static void Header(Utf8JsonWriter w, Guid session, string membership, int round,
        string sender, Guid id, DateTime time)
    {
        w.WriteStringValue(session); w.WriteStringValue(membership); w.WriteNumberValue(round);
        w.WriteStringValue(sender); w.WriteStringValue(id); w.WriteNumberValue(time.Ticks);
        w.WriteNumberValue((int)time.Kind);
    }
    private static void WriteReading(Utf8JsonWriter w, SignedReading r, bool includeSignature)
    {
        w.WriteStartArray(); w.WriteStringValue("swarm-reading/v1");
        Header(w, r.Session, r.Membership, r.Round, r.Sender, r.Id, r.Timestamp);
        w.WriteNumberValue(r.Value);
        if (includeSignature) w.WriteStringValue(r.Signature);
        w.WriteEndArray();
    }
    public static byte[] Unsigned(SignedReading r) => Encode(w => WriteReading(w, r, false));
    public static byte[] Unsigned(AgreementMessage m) => Encode(w =>
    {
        w.WriteStartArray(); w.WriteStringValue("swarm-agreement/v1"); w.WriteNumberValue((int)m.Kind);
        Header(w, m.Session, m.Membership, m.Round, m.Sender, m.Id, m.Timestamp);
        w.WriteStringValue(m.Digest);
        if (m.Reading is null) w.WriteNullValue(); else WriteReading(w, m.Reading, true);
        w.WriteStartArray(); foreach (var r in m.Readings) WriteReading(w, r, true); w.WriteEndArray();
        w.WriteEndArray();
    });
    public static string Digest(IReadOnlyList<SignedReading> readings) => Hash(Encode(w =>
    {
        w.WriteStartArray(); w.WriteStringValue("swarm-proposal/v1");
        foreach (var r in readings) WriteReading(w, r, true);
        w.WriteEndArray();
    }));
    public static string Hash(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload));
    public static bool IsDigest(string? digest) => digest is { Length: 64 } && digest.All(char.IsAsciiHexDigit);
}

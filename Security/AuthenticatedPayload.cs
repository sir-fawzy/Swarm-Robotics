using System.Text.Json;
using SwarmSecureCoordination.Models;
namespace SwarmSecureCoordination.Security;
public static class AuthenticatedPayload
{
    // Fixed property order, invariant JSON numbers, exact double round trips and UTC ticks.
    public static byte[] Encode(Message m)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray(); writer.WriteStringValue("SwarmSecureCoordination/v1");
            writer.WriteStringValue(m.RobotId); writer.WriteNumberValue(m.RoundId);
            writer.WriteNumberValue(m.Value); writer.WriteStringValue(m.MessageId);
            writer.WriteNumberValue(m.Timestamp.Ticks); writer.WriteNumberValue((int)m.Timestamp.Kind);
            writer.WriteStringValue(m.PrevHash); writer.WriteEndArray();
        }
        return stream.ToArray();
    }
}

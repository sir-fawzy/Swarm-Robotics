namespace SwarmSecureCoordination.Distributed;

public sealed record NetworkPacket(string From, string To, byte[] Payload);
public sealed record DeliveryRule(bool Drop = false, int DelayTicks = 1, int Copies = 1);

// A deterministic scheduler of independent inbox deliveries. It cannot set any node's result.
public sealed class SimulatedNetwork : IRobotNetwork
{
    private readonly Dictionary<string, Action<byte[]>> _endpoints = new(StringComparer.Ordinal);
    private readonly List<(int Tick, NetworkPacket Packet)> _pending = [];
    private readonly int _capacity;
    private readonly Random _random;
    public Func<NetworkPacket, DeliveryRule>? Rule { get; set; }
    public int Tick { get; private set; }
    public int Delivered { get; private set; }
    public int Dropped { get; private set; }
    public int MaxQueued { get; private set; }
    public int Pending => _pending.Count;
    public SimulatedNetwork(int capacity = 8192, int seed = 1)
    { if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity)); _capacity = capacity; _random = new(seed); }
    public void Register(string id, Action<byte[]> receive) => _endpoints.Add(id, receive);
    public void Broadcast(string sender, byte[] payload)
    { foreach (var id in _endpoints.Keys) if (id != sender) Send(sender, id, payload); }
    public void Send(string sender, string receiver, byte[] payload)
    {
        if (!_endpoints.ContainsKey(receiver) || payload.Length > ProtocolCodec.MaxPacketBytes) { Dropped++; return; }
        var packet = new NetworkPacket(sender, receiver, (byte[])payload.Clone());
        var rule = Rule?.Invoke(packet) ?? new DeliveryRule();
        if (rule.Drop) { Dropped++; return; }
        if (rule.DelayTicks is < 1 or > 100 || rule.Copies is < 1 or > 8) throw new ArgumentException("Invalid delivery rule");
        for (int i = 0; i < rule.Copies; i++)
        {
            if (_pending.Count == _capacity) { Dropped++; continue; }
            _pending.Add((checked(Tick + rule.DelayTicks), packet with { Payload = (byte[])packet.Payload.Clone() }));
        }
        MaxQueued = Math.Max(MaxQueued, _pending.Count);
    }
    public void Advance()
    {
        Tick++;
        var due = _pending.Where(p => p.Tick <= Tick).ToList();
        _pending.RemoveAll(p => p.Tick <= Tick);
        _random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(due));
        foreach (var item in due) { Delivered++; _endpoints[item.Packet.To](item.Packet.Payload); }
    }
    public void Run(int ticks) { for (int i = 0; i < ticks; i++) Advance(); }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace ChestCounter;

public sealed class FriendInfo
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public int Games { get; set; }
    public double Avg { get; set; }
    public int Best { get; set; }
    public int Total { get; set; }
    public bool Online { get; set; }
    public int SeasonMatches { get; set; }
    public double SeasonChests => SeasonMatches * Avg;
    /// <summary>Chests since the friend pressed New week, or -1 if they haven't.</summary>
    public int WeekChests { get; set; } = -1;
}

/// <summary>
/// Shares stats with friends in the same room through a public MQTT server.
/// Each copy publishes retained "stats" and "online" messages under
/// chestcounter/v1/{room}/{clientId}/..., so friends get your latest stats
/// as soon as they connect, and the server marks you offline if you drop.
/// </summary>
public sealed class FriendSync : IDisposable
{
    const string Broker = "broker.emqx.io";
    const int Port = 8883; // TLS
    const string Root = "chestcounter/v1";
    const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    record StatsPayload(string Name, int Count, int Games, double Avg, int Best, int Total, int SeasonMatches = 0,
        int WeekChests = -1);

    readonly Dispatcher ui;
    readonly IMqttClient client = new MqttFactory().CreateMqttClient();
    readonly Dictionary<string, FriendInfo> friends = new();
    readonly object gate = new();
    CancellationTokenSource? cts;
    string room = "";
    string me = "";
    string? latestStats;

    public event Action? Changed;
    public string Status { get; private set; } = "Off";
    public bool IsConnected => client.IsConnected;
    public bool IsRunning => cts != null;

    public FriendSync(Dispatcher ui)
    {
        this.ui = ui;
        client.ApplicationMessageReceivedAsync += OnMessage;
    }

    public IReadOnlyList<FriendInfo> Friends
    {
        get
        {
            lock (gate)
                return friends.Values
                    .Where(f => f.Name.Length > 0)
                    .OrderByDescending(f => f.Online)
                    .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
    }

    // ---- room codes ----

    public static string NewCode()
    {
        var chars = new char[10];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = CodeAlphabet[Random.Shared.Next(CodeAlphabet.Length)];
        return FormatCode(new string(chars));
    }

    public static string NormalizeCode(string code) =>
        new(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static string FormatCode(string code)
    {
        string n = NormalizeCode(code);
        return n.Length == 10 ? n[..5] + "-" + n[5..] : n;
    }

    public static bool IsValidCode(string code) => NormalizeCode(code).Length >= 6;

    // ---- lifecycle ----

    public void Start(AppSettings s)
    {
        Stop();
        room = NormalizeCode(s.SyncCode).ToLowerInvariant();
        me = s.SyncClientId;
        latestStats = BuildStats(s);
        lock (gate) friends.Clear();
        cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;
        SetStatus("Connecting…");
        Task.Run(() => Maintain(token));
    }

    public void Stop()
    {
        CancellationTokenSource? c = cts;
        if (c == null) return;
        cts = null;
        c.Cancel();
        // Tell friends we've gone offline, then disconnect. Bounded so quitting never hangs.
        Task.Run(async () =>
        {
            try
            {
                if (client.IsConnected)
                {
                    await Publish(Topic(me, "online"), "0");
                    await client.DisconnectAsync();
                }
            }
            catch (Exception)
            {
            }
        }).Wait(TimeSpan.FromSeconds(3));
        lock (gate) friends.Clear();
        SetStatus("Off");
    }

    public void PublishStats(AppSettings s)
    {
        latestStats = BuildStats(s);
        if (!client.IsConnected) return; // sent on (re)connect
        string stats = latestStats;
        Task.Run(async () =>
        {
            try { await Publish(Topic(me, "stats"), stats); }
            catch (Exception e) { AppSettings.Log($"Sync publish failed: {e.Message}"); }
        });
    }

    async Task Maintain(CancellationToken ct)
    {
        int delay = 2;
        while (!ct.IsCancellationRequested)
        {
            if (!client.IsConnected)
            {
                try
                {
                    MqttClientOptions options = new MqttClientOptionsBuilder()
                        .WithTcpServer(Broker, Port)
                        .WithTlsOptions(o => o.UseTls())
                        .WithClientId($"chestcounter-{me}-{Random.Shared.Next(1_000_000)}")
                        .WithCleanSession()
                        .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                        .WithWillTopic(Topic(me, "online"))
                        .WithWillPayload("0")
                        .WithWillRetain(true)
                        .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build();

                    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await client.ConnectAsync(options, connectTimeout.Token);

                    await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic($"{Root}/{room}/+/+")
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .Build(), ct);

                    await Publish(Topic(me, "online"), "1");
                    if (latestStats != null) await Publish(Topic(me, "stats"), latestStats);

                    delay = 2;
                    AppSettings.Log($"Sync connected to room {room}");
                    SetStatus("Connected");
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    AppSettings.Log($"Sync connect failed: {e.GetType().Name}: {e.Message}");
                    SetStatus("Can't reach the server, retrying…");
                    try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { return; }
                    delay = Math.Min(delay * 2, 60);
                    continue;
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }
            if (!client.IsConnected && !ct.IsCancellationRequested) SetStatus("Reconnecting…");
        }
    }

    Task OnMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        // chestcounter / v1 / room / clientId / kind
        string[] parts = e.ApplicationMessage.Topic.Split('/');
        if (parts.Length != 5 || parts[2] != room || parts[3] == me) return Task.CompletedTask;

        string id = parts[3];
        string payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
        lock (gate)
        {
            if (!friends.TryGetValue(id, out FriendInfo? f))
                friends[id] = f = new FriendInfo { Id = id };

            if (parts[4] == "online")
            {
                f.Online = payload == "1";
            }
            else if (parts[4] == "stats")
            {
                try
                {
                    StatsPayload? p = JsonSerializer.Deserialize<StatsPayload>(payload, Json);
                    if (p != null)
                    {
                        f.Name = p.Name.Length > 20 ? p.Name[..20] : p.Name;
                        f.Count = p.Count;
                        f.Games = p.Games;
                        f.Avg = p.Avg;
                        f.Best = p.Best;
                        f.Total = p.Total;
                        f.SeasonMatches = p.SeasonMatches;
                        f.WeekChests = p.WeekChests;
                    }
                }
                catch (Exception)
                {
                    // Ignore malformed messages.
                }
            }
        }
        ui.InvokeAsync(() => Changed?.Invoke());
        return Task.CompletedTask;
    }

    // ---- helpers ----

    string Topic(string clientId, string kind) => $"{Root}/{room}/{clientId}/{kind}";

    Task Publish(string topic, string payload) =>
        client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag()
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

    static string BuildStats(AppSettings s) =>
        JsonSerializer.Serialize(new StatsPayload(
            s.SyncName, s.Count, s.Games.Count, Math.Round(s.Average, 2), s.Best, s.Total, s.SeasonMatches,
            s.WeekChests ?? -1), Json);

    void SetStatus(string status)
    {
        Status = status;
        ui.InvokeAsync(() => Changed?.Invoke());
    }

    public void Dispose()
    {
        Stop();
        client.Dispose();
    }
}

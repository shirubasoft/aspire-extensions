using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TigerBeetle;

const string CdcExchange = "tigerbeetle";
const string CdcQueue = "tigerbeetle-sample";

var builder = WebApplication.CreateBuilder(args);

var clusterId = builder.Configuration["TIGERBEETLE_CLUSTERID"]
    ?? throw new InvalidOperationException("TIGERBEETLE_CLUSTERID is required.");
var addressConfiguration = builder.Configuration.GetSection("TIGERBEETLE_ADDRESSES");
var addresses = (addressConfiguration.GetChildren().Any()
    ? addressConfiguration.Get<string[]>()
    : addressConfiguration.Value?.Split(
        ',',
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    ?? throw new InvalidOperationException("TIGERBEETLE_ADDRESSES is required.");
var resolvedAddresses = await ResolveAddressesAsync(addresses, CancellationToken.None);

using var tigerBeetleClient = new Client(
    clusterID: UInt128.Parse(clusterId, CultureInfo.InvariantCulture),
    addresses: resolvedAddresses);
await EnsureSampleAccountsAsync(tigerBeetleClient);

var eventStore = new CdcEventStore(capacity: 100);
builder.Services.AddSingleton(tigerBeetleClient);
builder.Services.AddSingleton(eventStore);

var app = builder.Build();

app.Logger.LogInformation(
    "Connected to TigerBeetle cluster {ClusterId} at {Addresses}. Sample accounts are ready.",
    clusterId,
    string.Join(',', resolvedAddresses));

var rabbitMqConnectionString = builder.Configuration.GetConnectionString("rabbitmq")
    ?? throw new InvalidOperationException("The rabbitmq connection string is required.");
var rabbitMqFactory = new ConnectionFactory
{
    Uri = new Uri(rabbitMqConnectionString),
    ClientProvidedName = "aspire-tigerbeetle-cdc-sample",
};
await using var rabbitMqConnection = await rabbitMqFactory.CreateConnectionAsync();
await using var rabbitMqChannel = await rabbitMqConnection.CreateChannelAsync();
await rabbitMqChannel.ExchangeDeclareAsync(
    CdcExchange,
    ExchangeType.Fanout,
    durable: true,
    autoDelete: false,
    arguments: null,
    passive: false,
    noWait: false);
await rabbitMqChannel.QueueDeclareAsync(
    CdcQueue,
    durable: true,
    exclusive: false,
    autoDelete: false,
    arguments: null,
    passive: false,
    noWait: false);
await rabbitMqChannel.QueueBindAsync(
    CdcQueue,
    CdcExchange,
    routingKey: string.Empty,
    arguments: null,
    noWait: false);

var consumer = new AsyncEventingBasicConsumer(rabbitMqChannel);
consumer.ReceivedAsync += async (_, delivery) =>
{
    using var document = JsonDocument.Parse(delivery.Body);
    var storedEventCount = eventStore.Add(document.RootElement.Clone());
    await rabbitMqChannel.BasicAckAsync(delivery.DeliveryTag, multiple: false);

    app.Logger.LogInformation(
        "Received TigerBeetle CDC delivery {DeliveryTag} from exchange {Exchange} with {BodyLength} bytes. Stored event count is {StoredEventCount}.",
        delivery.DeliveryTag,
        delivery.Exchange,
        delivery.Body.Length,
        storedEventCount);
};
await rabbitMqChannel.BasicConsumeAsync(CdcQueue, autoAck: false, consumer);

app.Logger.LogInformation(
    "RabbitMQ CDC consumer is ready. Queue {Queue} is bound to exchange {Exchange}.",
    CdcQueue,
    CdcExchange);

app.MapGet("/", () => Results.Ok(new
{
    clusterId,
    addresses = string.Join(',', addresses),
    cdcExchange = CdcExchange,
    cdcQueue = CdcQueue,
    message = "TigerBeetle and RabbitMQ connection properties were injected by Aspire."
}));

app.MapGet("/health", async (Client client, CancellationToken cancellationToken) =>
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(5));
    await client.LookupAccountsAsync(new UInt128[] { 1 }).WaitAsync(timeout.Token);
    return Results.Ok();
});

app.MapGet("/accounts/{id}", async (string id, Client client) =>
{
    var accountId = UInt128.Parse(id, CultureInfo.InvariantCulture);
    var accounts = await client.LookupAccountsAsync(new UInt128[] { accountId });
    return Results.Ok(accounts);
});

app.MapPost("/transfers", async (Client client) =>
{
    var transfer = new Transfer
    {
        Id = ID.Create(),
        DebitAccountId = 1,
        CreditAccountId = 2,
        Amount = 100,
        Ledger = 1,
        Code = 1,
    };
    var results = await client.CreateTransfersAsync(new Transfer[] { transfer }).WaitAsync(TimeSpan.FromSeconds(30));

    if (results[0].Status != CreateTransferStatus.Created)
    {
        app.Logger.LogWarning(
            "TigerBeetle rejected transfer {TransferId} with status {Status}.",
            transfer.Id,
            results[0].Status);

        return Results.Conflict(new
        {
            result = results[0].Status.ToString(),
        });
    }

    app.Logger.LogInformation(
        "Created TigerBeetle transfer {TransferId} from account {DebitAccountId} to {CreditAccountId} for {Amount}.",
        transfer.Id,
        transfer.DebitAccountId,
        transfer.CreditAccountId,
        transfer.Amount);

    return Results.Created($"/transfers/{transfer.Id}", new
    {
        id = transfer.Id.ToString(CultureInfo.InvariantCulture),
        amount = transfer.Amount.ToString(CultureInfo.InvariantCulture),
    });
});

app.MapGet("/cdc/events", (CdcEventStore events) => Results.Ok(events.Snapshot()));

await app.RunAsync();

static async Task EnsureSampleAccountsAsync(Client client)
{
    var results = await client.CreateAccountsAsync(new Account[]
    {
        new Account { Id = 1, Ledger = 1, Code = 1 },
        new Account { Id = 2, Ledger = 1, Code = 1 },
    }).WaitAsync(TimeSpan.FromSeconds(30));
    var unexpected = results
        .Where(result => result.Status is not (CreateAccountStatus.Created or CreateAccountStatus.Exists))
        .ToArray();

    if (unexpected.Length > 0)
    {
        throw new InvalidOperationException(
            $"Could not create sample accounts: {string.Join(", ", unexpected.Select(result => result.Status))}");
    }
}

static async Task<string[]> ResolveAddressesAsync(string[] values, CancellationToken cancellationToken)
{
    var addresses = values.ToArray();

    for (var index = 0; index < addresses.Length; index++)
    {
        if (int.TryParse(addresses[index], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            continue;
        }

        if (!Uri.TryCreate($"tcp://{addresses[index]}", UriKind.Absolute, out var endpoint))
        {
            throw new FormatException($"'{addresses[index]}' is not a valid TigerBeetle address.");
        }

        if (IPAddress.TryParse(endpoint.DnsSafeHost, out _))
        {
            continue;
        }

        var resolved = await Dns.GetHostAddressesAsync(endpoint.DnsSafeHost, cancellationToken);
        var ipAddress = resolved.FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? resolved.FirstOrDefault()
            ?? throw new InvalidOperationException($"Could not resolve {endpoint.DnsSafeHost}.");
        addresses[index] = ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{ipAddress}]:{endpoint.Port}"
            : $"{ipAddress}:{endpoint.Port}";
    }

    return addresses;
}

sealed class CdcEventStore(int capacity)
{
    private readonly ConcurrentQueue<JsonElement> _events = new();

    public int Add(JsonElement value)
    {
        _events.Enqueue(value);

        while (_events.Count > capacity)
        {
            _events.TryDequeue(out _);
        }

        return _events.Count;
    }

    public JsonElement[] Snapshot() => _events.ToArray();
}

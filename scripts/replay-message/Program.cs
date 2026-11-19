using System.Text.Json;
using System.Text.Json.Nodes;
using RabbitMQ.Client;

try
{
    var options = ParseArguments(args);
    if (!options.TryGetValue("--source", out var source) ||
        !options.TryGetValue("--destination", out var destination) ||
        !options.TryGetValue("--event-id", out var id) ||
        !Guid.TryParseExact(id, "D", out var eventId) || eventId == Guid.Empty ||
        destination is not ("worker-habit-events-v1" or "notifications-ready-v1") ||
        (source != destination + "_error" && source != destination + "_skipped"))
    {
        throw new ArgumentException("Specify the source queue, its original destination endpoint, and a nonempty event UUID.");
    }

    var limit = 100;
    if (options.TryGetValue("--limit", out var scanLimit) &&
        (!int.TryParse(scanLimit, out limit) || limit is < 1 or > 1000))
        throw new ArgumentException("--limit must be between 1 and 1000.");

    var uri = new Uri(RequiredEnvironment("KONTUR_REPLAY_AMQP_URI"));
    if (uri.Scheme is not ("amqp" or "amqps") || uri.UserInfo.Length != 0 ||
        uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        throw new ArgumentException("KONTUR_REPLAY_AMQP_URI must contain only the AMQP scheme, host and optional port.");

    var factory = new ConnectionFactory
    {
        AutomaticRecoveryEnabled = false,
        Uri = uri,
        VirtualHost = Environment.GetEnvironmentVariable("RabbitMq__VirtualHost") ?? "habit-tracker",
        UserName = RequiredEnvironment("RabbitMq__Username"),
        Password = RequiredEnvironment("RabbitMq__Password")
    };
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var ct = timeout.Token;
    await using var connection = await factory.CreateConnectionAsync(ct);
    await using var reader = await connection.CreateChannelAsync(cancellationToken: ct);
    for (var index = 0; index < limit; index++)
    {
        // Keep scanned copies unacknowledged so the same copy cannot be fetched
        // twice. Closing the channel returns all copies left unacknowledged.
        var delivery = await reader.BasicGetAsync(source, autoAck: false, cancellationToken: ct);
        if (delivery is null)
            break;
        if (!Guid.TryParse(delivery.BasicProperties.MessageId, out var messageId) || messageId != eventId)
            continue;

        var envelope = JsonNode.Parse(delivery.Body.Span) as JsonObject;
        if (envelope?["message"] is not JsonObject message ||
            !Guid.TryParse(message["eventId"]?.GetValue<string>(), out var payloadId) || payloadId != eventId)
            throw new InvalidOperationException("AMQP MessageId and envelope eventId do not match.");

        var body = delivery.Body;
        if (options.TryGetValue("--message", out var path))
        {
            var corrected = JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject
                ?? throw new ArgumentException("--message requires a JSON object containing the corrected message.");
            foreach (var field in new[] { "eventId", "correlationId", "notificationId", "userId" })
                if (!JsonNode.DeepEquals(message[field], corrected[field]))
                    throw new ArgumentException($"Corrected message changes the identity field '{field}'.");
            envelope["message"] = corrected;
            body = JsonSerializer.SerializeToUtf8Bytes(envelope);
        }

        Console.WriteLine($"Found event {eventId:D} in {source}. Destination: {destination}. Scanned: {index + 1}.");
        if (!options.ContainsKey("--publish"))
        {
            Console.WriteLine("Dry run. The delivery will be returned to the source queue.");
            return 0;
        }

        var publisherOptions = new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);
        await using var publisher = await connection.CreateChannelAsync(publisherOptions, ct);
        await publisher.BasicPublishAsync("", destination, mandatory: true,
            new BasicProperties(delivery.BasicProperties), body, ct);
        Console.WriteLine("Broker confirmed publication. Verify the consumer result and confirm there is no new fault.");
        if (!Console.IsInputRedirected)
        {
            Console.WriteLine("Type the event ID to acknowledge this exact DLQ delivery, or press Enter to return it.");
            if (Guid.TryParseExact(Console.ReadLine(), "D", out var confirmed) && confirmed == eventId)
            {
                await reader.BasicAckAsync(delivery.DeliveryTag, multiple: false);
                Console.WriteLine("The selected DLQ delivery was acknowledged.");
                return 0;
            }
        }
        Console.WriteLine("The original copy will be returned to the source queue.");
        return 0;
    }
    Console.Error.WriteLine($"Event {eventId:D} was not found among the first {limit} ready copies in {source}.");
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine("dotnet run --project scripts/replay-message -- --source <queue> --destination <endpoint> --event-id <uuid> [--limit <count>] [--message <file>] [--dry-run | --publish]");
    return 1;
}

static string RequiredEnvironment(string key) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : throw new ArgumentException($"Set {key}.");

static Dictionary<string, string> ParseArguments(string[] arguments)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < arguments.Length; index++)
    {
        var key = arguments[index];
        if (key is "--publish" or "--dry-run")
        {
            options.Add(key, "");
            continue;
        }
        if (key is not ("--source" or "--destination" or "--event-id" or "--limit" or "--message") ||
            index + 1 == arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Invalid argument '{key}'.");
        options.Add(key, arguments[++index]);
    }
    if (options.ContainsKey("--publish") && options.ContainsKey("--dry-run"))
        throw new ArgumentException("Choose either --publish or --dry-run.");
    return options;
}

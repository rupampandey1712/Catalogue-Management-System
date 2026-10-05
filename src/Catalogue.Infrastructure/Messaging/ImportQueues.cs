using System.Text.Json;
using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
using Catalogue.Application;
using Catalogue.Contracts;
using Microsoft.Extensions.Configuration;

namespace Catalogue.Infrastructure.Messaging;

/// <summary>In-process queue using a bounded channel; the default for local development.</summary>
public sealed class InMemoryImportQueue : IImportQueue
{
    private readonly Channel<ImportRequested> _channel =
        Channel.CreateBounded<ImportRequested>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

    public ValueTask PublishAsync(ImportRequested message, CancellationToken ct)
        => _channel.Writer.WriteAsync(message, ct);

    public async ValueTask<ImportRequested?> ReceiveAsync(CancellationToken ct)
        => await _channel.Reader.ReadAsync(ct);
}

/// <summary>Azure Service Bus queue (works against the local Service Bus emulator too).</summary>
public sealed class ServiceBusImportQueue : IImportQueue, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;
    private readonly ServiceBusReceiver _receiver;

    public ServiceBusImportQueue(IConfiguration config)
    {
        var connStr = config.GetConnectionString("ServiceBus")
            ?? throw new InvalidOperationException("ConnectionStrings:ServiceBus is required.");
        var queue = config["ServiceBus:QueueName"] ?? "catalogue-imports";
        _client = new ServiceBusClient(connStr, new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions
            {
                Mode = ServiceBusRetryMode.Exponential,
                MaxRetries = 6,
                Delay = TimeSpan.FromMilliseconds(500),
                MaxDelay = TimeSpan.FromSeconds(30),
                TryTimeout = TimeSpan.FromSeconds(60)
            }
        });
        _sender = _client.CreateSender(queue);
        _receiver = _client.CreateReceiver(queue, new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock
        });
    }

    public async ValueTask PublishAsync(ImportRequested message, CancellationToken ct)
    {
        var sbMessage = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(message))
        {
            MessageId = $"import-{message.ImportId}", // duplicate detection window on SB
            ContentType = "application/json",
            CorrelationId = message.CorrelationId
        };
        sbMessage.ApplicationProperties["correlationId"] = message.CorrelationId;
        await _sender.SendMessageAsync(sbMessage, ct);
    }

    public async ValueTask<ImportRequested?> ReceiveAsync(CancellationToken ct)
    {
        var msg = await _receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5), ct);
        if (msg is null) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<ImportRequested>(msg.Body.ToArray());
            await _receiver.CompleteMessageAsync(msg, ct);
            return payload;
        }
        catch
        {
            await _receiver.AbandonMessageAsync(msg, cancellationToken: ct);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _receiver.DisposeAsync();
        await _client.DisposeAsync();
    }
}

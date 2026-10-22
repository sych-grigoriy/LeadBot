using System.Threading.Channels;
using LeadBot.Domain.Leads;

namespace LeadBot.Application.Messaging;

public enum IncomingKind
{
    /// <summary>Обычное сообщение клиента.</summary>
    ClientText,
    /// <summary>Клиент нажал Start / перешёл по deep-link (payload — откуда пришёл).</summary>
    Start,
    /// <summary>Владелец сам ответил клиенту в своём Telegram (Business) — бот должен замолчать.</summary>
    ManagerReply
}

/// <summary>Нормализованное входящее сообщение. Адаптеры каналов приводят свои апдейты к этому виду.</summary>
public sealed record IncomingMessage(
    ChannelIdentity From,
    IncomingKind Kind,
    string Text,
    string ExternalId,
    DateTimeOffset At,
    string? StartPayload = null);

/// <summary>
/// Очередь между вебхуками и обработкой: вебхук отвечает 200 сразу, LLM работает в фоне.
/// Для одного инстанса — in-memory Channel; при масштабировании заменить на брокер (RabbitMQ/Redis Streams).
/// </summary>
public sealed class IncomingMessageQueue
{
    private readonly Channel<IncomingMessage> _channel =
        Channel.CreateBounded<IncomingMessage>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    public ValueTask EnqueueAsync(IncomingMessage message, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(message, ct);

    public IAsyncEnumerable<IncomingMessage> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}

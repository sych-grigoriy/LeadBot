namespace LeadBot.Domain.Leads;

/// <summary>
/// Как найти клиента и куда ему отвечать в конкретном мессенджере.
/// </summary>
public sealed record ChannelIdentity(
    ChannelKind Channel,
    /// <summary>ID пользователя в мессенджере (стабильный ключ лида).</summary>
    string UserId,
    /// <summary>ID чата, куда отправлять ответ.</summary>
    string ChatId,
    string? DisplayName = null,
    string? Username = null,
    /// <summary>Telegram Business: ID подключения, от имени которого бот отвечает в вашем личном чате.</summary>
    string? BusinessConnectionId = null)
{
    /// <summary>Ключ для поиска лида: один клиент в одном канале = один лид.</summary>
    public string Key => $"{Channel}:{UserId}";
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeadBot.Application.Abstractions;

/// <summary>
/// Провайдер-независимая модель диалога с LLM с поддержкой вызова инструментов (tool use / function calling).
/// Реализации: Anthropic (в каркасе), при необходимости — GigaChat, YandexGPT, OpenAI-совместимые.
/// </summary>
public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}

public enum LlmRole { User, Assistant }

public abstract record LlmContent;
public sealed record LlmText(string Text) : LlmContent;
public sealed record LlmToolUse(string Id, string Name, JsonElement Input) : LlmContent;
public sealed record LlmToolResult(string ToolUseId, string Content, bool IsError = false) : LlmContent;
/// <summary>Рассуждения модели (DeepSeek thinking mode) — клиенту не показываются, но провайдер может требовать вернуть их обратно.</summary>
public sealed record LlmReasoning(string Text) : LlmContent;

public sealed record LlmMessage(LlmRole Role, IReadOnlyList<LlmContent> Content)
{
    public static LlmMessage UserText(string text) => new(LlmRole.User, [new LlmText(text)]);
    public static LlmMessage AssistantText(string text) => new(LlmRole.Assistant, [new LlmText(text)]);
}

public sealed record LlmTool(string Name, string Description, JsonObject InputSchema);

public sealed record LlmRequest(
    string System,
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<LlmTool> Tools,
    int MaxTokens = 1024);

public sealed record LlmResponse(IReadOnlyList<LlmContent> Content, string? StopReason)
{
    public IEnumerable<LlmToolUse> ToolCalls => Content.OfType<LlmToolUse>();
    public string Text => string.Join("\n", Content.OfType<LlmText>().Select(t => t.Text)).Trim();
}

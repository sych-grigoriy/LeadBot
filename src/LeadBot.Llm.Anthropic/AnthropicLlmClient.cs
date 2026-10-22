using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeadBot.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LeadBot.Llm.Anthropic;

public sealed class AnthropicOptions
{
    public const string Section = "Llm:Anthropic";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    /// <summary>Идентификатор модели — возьмите актуальный из документации Anthropic.</summary>
    public string Model { get; set; } = "";
    public double Temperature { get; set; } = 0.4;
}

/// <summary>
/// Клиент Anthropic Messages API с tool use на чистом HttpClient.
/// Другого провайдера (GigaChat, YandexGPT, OpenAI-совместимый) подключайте отдельной реализацией ILlmClient.
/// </summary>
public sealed class AnthropicLlmClient(HttpClient http, IOptions<AnthropicOptions> options) : ILlmClient
{
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var o = options.Value;
        var body = new JsonObject
        {
            ["model"] = o.Model,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = o.Temperature,
            ["system"] = request.System,
            ["messages"] = new JsonArray(request.Messages.Select(ToJson).ToArray<JsonNode?>()),
            ["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode?)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.InputSchema.DeepClone()
            }).ToArray())
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Add("x-api-key", o.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API {(int)resp.StatusCode}: {text}");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var content = new List<LlmContent>();
        foreach (var block in root.GetProperty("content").EnumerateArray())
        {
            switch (block.GetProperty("type").GetString())
            {
                case "text":
                    content.Add(new LlmText(block.GetProperty("text").GetString() ?? ""));
                    break;
                case "tool_use":
                    content.Add(new LlmToolUse(
                        block.GetProperty("id").GetString()!,
                        block.GetProperty("name").GetString()!,
                        block.GetProperty("input").Clone()));
                    break;
            }
        }
        return new LlmResponse(content, root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null);
    }

    private static JsonNode ToJson(LlmMessage m) => new JsonObject
    {
        ["role"] = m.Role == LlmRole.User ? "user" : "assistant",
        ["content"] = new JsonArray(m.Content.Where(c => c is not LlmReasoning).Select(c => (JsonNode?)(c switch
        {
            LlmText t => new JsonObject { ["type"] = "text", ["text"] = t.Text },
            LlmToolUse u => new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = u.Id,
                ["name"] = u.Name,
                ["input"] = JsonNode.Parse(u.Input.GetRawText())
            },
            LlmToolResult r => new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = r.ToolUseId,
                ["content"] = r.Content,
                ["is_error"] = r.IsError
            },
            _ => throw new NotSupportedException(c.GetType().Name)
        })).ToArray())
    };
}

public static class AnthropicExtensions
{
    public static IServiceCollection AddAnthropicLlm(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AnthropicOptions>(config.GetSection(AnthropicOptions.Section));
        services.AddHttpClient<ILlmClient, AnthropicLlmClient>(c => c.Timeout = TimeSpan.FromSeconds(90));
        return services;
    }
}

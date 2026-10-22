using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeadBot.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LeadBot.Llm.DeepSeek;

public sealed class DeepSeekOptions
{
    public const string Section = "Llm:DeepSeek";

    public string ApiKey { get; set; } = "";
    /// <summary>OpenAI-совместимый endpoint DeepSeek.</summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    /// <summary>deepseek-flash — быстрая и дешёвая; deepseek-v4-pro — умнее и дороже. Актуальный список — api-docs.deepseek.com.</summary>
    public string Model { get; set; } = "deepseek-flash";
    public double Temperature { get; set; } = 0.4;
    /// <summary>
    /// Режим рассуждений. У DeepSeek он включён по умолчанию, но с инструментами требует возвращать
    /// reasoning_content всех прошлых ходов. Мы храним историю как текст, поэтому держим его выключенным.
    /// </summary>
    public bool EnableThinking { get; set; }
}

/// <summary>
/// Клиент DeepSeek Chat Completions (формат OpenAI) с function calling.
/// Подходит и для других OpenAI-совместимых провайдеров — достаточно поменять BaseUrl и Model.
/// </summary>
public sealed class DeepSeekLlmClient(HttpClient http, IOptions<DeepSeekOptions> options) : ILlmClient
{
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var o = options.Value;

        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = request.System } };
        foreach (var m in request.Messages)
            foreach (var node in ToOpenAi(m))
                messages.Add(node);

        var body = new JsonObject
        {
            ["model"] = o.Model,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxTokens,
            ["thinking"] = new JsonObject { ["type"] = o.EnableThinking ? "enabled" : "disabled" },
            ["stream"] = false
        };
        if (!o.EnableThinking) body["temperature"] = o.Temperature;

        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode?)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.InputSchema.DeepClone()
                }
            }).ToArray());
            body["tool_choice"] = "auto";
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);

        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"DeepSeek API {(int)resp.StatusCode}: {text}");

        using var doc = JsonDocument.Parse(text);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var msg = choice.GetProperty("message");
        var content = new List<LlmContent>();

        if (msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String && rc.GetString() is { Length: > 0 } reasoning)
            content.Add(new LlmReasoning(reasoning));

        if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } answer)
            content.Add(new LlmText(answer));

        if (msg.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                var fn = call.GetProperty("function");
                content.Add(new LlmToolUse(
                    call.GetProperty("id").GetString()!,
                    fn.GetProperty("name").GetString()!,
                    ParseArguments(fn.GetProperty("arguments").GetString())));
            }
        }

        var finish = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
        return new LlmResponse(content, finish);
    }

    /// <summary>Провайдер-независимое сообщение → одно или несколько сообщений формата OpenAI.</summary>
    private static IEnumerable<JsonNode> ToOpenAi(LlmMessage m)
    {
        if (m.Role == LlmRole.Assistant)
        {
            var text = string.Join("\n", m.Content.OfType<LlmText>().Select(t => t.Text));
            var reasoning = string.Join("\n", m.Content.OfType<LlmReasoning>().Select(r => r.Text));
            var toolUses = m.Content.OfType<LlmToolUse>().ToList();

            var node = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = text.Length > 0 ? text : null
            };
            if (reasoning.Length > 0) node["reasoning_content"] = reasoning;
            if (toolUses.Count > 0)
            {
                node["tool_calls"] = new JsonArray(toolUses.Select(u => (JsonNode?)new JsonObject
                {
                    ["id"] = u.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = u.Name,
                        ["arguments"] = u.Input.GetRawText()
                    }
                }).ToArray());
            }
            yield return node;
            yield break;
        }

        // user: результаты инструментов идут отдельными сообщениями role=tool, текст — role=user.
        foreach (var r in m.Content.OfType<LlmToolResult>())
        {
            yield return new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = r.ToolUseId,
                ["content"] = r.IsError ? $"ОШИБКА: {r.Content}" : r.Content
            };
        }

        var userText = string.Join("\n", m.Content.OfType<LlmText>().Select(t => t.Text));
        if (userText.Length > 0)
            yield return new JsonObject { ["role"] = "user", ["content"] = userText };
    }

    /// <summary>arguments приходят строкой JSON; при мусоре отдаём пустой объект — инструмент вернёт модели понятную ошибку.</summary>
    private static JsonElement ParseArguments(string? raw)
    {
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            return d.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }
}

public static class DeepSeekExtensions
{
    public static IServiceCollection AddDeepSeekLlm(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<DeepSeekOptions>(config.GetSection(DeepSeekOptions.Section));
        services.AddHttpClient<ILlmClient, DeepSeekLlmClient>(c => c.Timeout = TimeSpan.FromSeconds(120));
        return services;
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToDoBot.Tests;

internal sealed class TestTelegramHttpHandler : HttpMessageHandler
{
    private int _nextMessageId = 1000;

    public List<TestTelegramRequest> Requests { get; } = [];
    public int LastSentMessageId { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.RequestUri!.AbsolutePath.Split('/').Last();
        var raw = request.Content is null ? "{}" :
            await request.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(raw);
        var body = document.RootElement.Clone();
        Requests.Add(new TestTelegramRequest(method, body));

        JsonNode result;
        if (method is "answerCallbackQuery" or "setMyCommands")
        {
            result = JsonValue.Create(true)!;
        }
        else if (method is "sendMessage" or "editMessageText")
        {
            var messageId = method == "sendMessage" ? _nextMessageId++ :
                body.GetProperty("message_id").GetInt32();
            if (method == "sendMessage")
                LastSentMessageId = messageId;
            var chatValue = body.GetProperty("chat_id");
            var chatId = chatValue.ValueKind == JsonValueKind.Number ? chatValue.GetInt64() :
                long.Parse(chatValue.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            result = new JsonObject
            {
                ["message_id"] = messageId,
                ["date"] = 1735689600,
                ["chat"] = new JsonObject
                {
                    ["id"] = chatId,
                    ["type"] = "private",
                    ["first_name"] = "Offline tester"
                },
                ["from"] = new JsonObject
                {
                    ["id"] = 123456789,
                    ["is_bot"] = true,
                    ["first_name"] = "Offline bot",
                    ["username"] = "offline_todo_bot"
                },
                ["text"] = body.GetProperty("text").GetString()
            };
            if (body.TryGetProperty("reply_markup", out var markup))
                result["reply_markup"] = JsonNode.Parse(markup.GetRawText());
        }
        else
        {
            throw new InvalidOperationException($"Unexpected offline Telegram method: {method}");
        }

        var payload = new JsonObject { ["ok"] = true, ["result"] = result };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }
}

internal sealed record TestTelegramRequest(string Method, JsonElement Body)
{
    public string Text => Body.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";

    public IReadOnlyList<TestTelegramButton> Buttons
    {
        get
        {
            if (!Body.TryGetProperty("reply_markup", out var markup) ||
                !markup.TryGetProperty("inline_keyboard", out var rows))
                return [];
            return rows.EnumerateArray().SelectMany(row => row.EnumerateArray())
                .Select(button => new TestTelegramButton(
                    button.GetProperty("text").GetString()!,
                    button.GetProperty("callback_data").GetString()!))
                .ToArray();
        }
    }
}

internal sealed record TestTelegramButton(string Text, string Data);

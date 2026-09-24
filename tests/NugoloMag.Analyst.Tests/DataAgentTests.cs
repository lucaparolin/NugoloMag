using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using NugoloMag.Analyst.Application.Assistant;
using NugoloMag.Analyst.Domain.Assistant;
using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Infrastructure.Llm;

namespace NugoloMag.Analyst.Tests;

public class ReadOnlySqlGuardTests
{
    [Theory]
    [InlineData("SELECT TOP 10 * FROM dbo.MovMag")]
    [InlineData("WITH x AS (SELECT CodArt, SUM(Quantita) q FROM dbo.MovMag GROUP BY CodArt) SELECT * FROM x ORDER BY q DESC;")]
    [InlineData("SELECT [Delete], [Update] FROM dbo.T -- DROP TABLE x")]
    [InlineData("SELECT 'DELETE FROM x; DROP' AS testo")]
    [InlineData("select a from b /* exec xp_cmdshell */ where c = 1 option (maxrecursion 0)")]
    public void Read_only_queries_are_allowed(string sql) => Assert.True(ReadOnlySqlGuard.Check(sql).IsAllowed, ReadOnlySqlGuard.Check(sql).Reason);

    [Theory]
    [InlineData("DELETE FROM dbo.MovMag")]
    [InlineData("SELECT 1; DROP TABLE dbo.MovMag")]
    [InlineData("SELECT * INTO dbo.Copia FROM dbo.MovMag")]
    [InlineData("EXEC xp_cmdshell 'dir'")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1')")]
    [InlineData("WITH x AS (SELECT 1 a) UPDATE t SET a = 1")]
    [InlineData("SELECT 1 /* commento non chiuso")]
    [InlineData("SELECT 'stringa non chiusa")]
    [InlineData("DECLARE @x int = 1 SELECT @x")]
    [InlineData("SELECT dbo.fn() ; WAITFOR DELAY '00:00:10'")]
    public void Writes_and_side_effects_are_rejected(string sql) => Assert.False(ReadOnlySqlGuard.Check(sql).IsAllowed);
}

/// <summary>
/// Stesso scenario (elenco tabelle → query → risposta) su tutti i fornitori: il ciclo agentico è identico,
/// cambia solo il protocollo. Ogni finto server risponde nel formato del fornitore e registra le richieste.
/// </summary>
public class LlmProviderTests
{
    private sealed class FakeApi(params string[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[_next++], Encoding.UTF8, "application/json") };
        }
    }

    private sealed class RecordingToolbox : IToolbox
    {
        public List<(string Name, string Input)> Calls { get; } = [];
        public IReadOnlyList<ToolSpec> Specs { get; } =
        [
            new("list_tables", "Elenca tabelle", """{"type":"object","properties":{"filter":{"type":"string"}}}"""),
            new("run_query", "Esegue query", """{"type":"object","properties":{"sql":{"type":"string"},"purpose":{"type":"string"}},"required":["sql","purpose"]}""")
        ];

        public Task<ToolOutcome> ExecuteAsync(string name, JsonElement input, CancellationToken ct = default)
        {
            Calls.Add((name, input.GetRawText()));
            return Task.FromResult(name == "list_tables" ? new ToolOutcome("dbo.MovMag\t8.937 righe", false) : new ToolOutcome("N\n8937\n1 righe, 0.01s", false));
        }
    }

    private static readonly AgentContext Context = new("erp", "GestionaleDemo", "Movimenti in dbo.MovMag.", new DateOnly(2026, 9, 24));
    private const string Sql = "SELECT COUNT(*) AS N FROM dbo.MovMag";

    private static async Task<(IReadOnlyList<ConversationEntry> Entries, RecordingToolbox Tools)> Run(IChatModel model)
    {
        var tools = new RecordingToolbox();
        var entries = await new LlmDataAgent(TestLlm.For(model)).ReplyAsync(Context, [], "Quanti movimenti ci sono?", tools);
        return (entries, tools);
    }

    private static void AssertScenario(IReadOnlyList<ConversationEntry> entries, RecordingToolbox tools)
    {
        Assert.Equal(["list_tables", "run_query"], tools.Calls.Select(c => c.Name));
        Assert.Contains("SELECT COUNT(*)", tools.Calls[1].Input);
        Assert.Equal("Ci sono 8.937 movimenti.", entries[^1].Text);
        Assert.Equal(2, entries.Count(e => e.Kind == EntryKind.ToolCall));
    }

    [Fact]
    public async Task Anthropic_provider_runs_the_tool_loop_through_the_official_sdk()
    {
        static string Msg(string stop, string content) =>
            "{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5\",\"content\":" + content +
            ",\"stop_reason\":\"" + stop + "\",\"stop_sequence\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";
        var api = new FakeApi(
            Msg("tool_use", """[{"type":"text","text":"Cerco la tabella."},{"type":"tool_use","id":"toolu_1","name":"list_tables","input":{"filter":"mov"}}]"""),
            Msg("tool_use", $$$"""[{"type":"tool_use","id":"toolu_2","name":"run_query","input":{"sql":"{{{Sql}}}","purpose":"contare"}}]"""),
            Msg("end_turn", """[{"type":"text","text":"Ci sono 8.937 movimenti."}]"""));
        var client = new AnthropicClient { ApiKey = "test", HttpClient = new HttpClient(api), MaxRetries = 0 };

        var (entries, tools) = await Run(new AnthropicChatModel(client, new LlmOptions { Provider = "anthropic", Model = "claude-opus-5" }));

        AssertScenario(entries, tools);
        Assert.Equal(EntryKind.Assistant, entries[0].Kind); // testo intermedio prima degli strumenti
        using var second = JsonDocument.Parse(api.Requests[1].Body);
        var messages = second.RootElement.GetProperty("messages");
        Assert.Contains("\"tool_result\"", messages[2].GetRawText());
        Assert.Contains("toolu_1", messages[2].GetRawText());
        Assert.Contains("GestionaleDemo", second.RootElement.GetProperty("system").GetRawText());
    }

    [Fact]
    public async Task OpenAi_compatible_provider_sends_functions_and_tool_messages()
    {
        static string Msg(string finish, string message) => "{\"choices\":[{\"index\":0,\"finish_reason\":\"" + finish + "\",\"message\":" + message + "}]}";
        var api = new FakeApi(
            Msg("tool_calls", """{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"list_tables","arguments":"{\"filter\":\"mov\"}"}}]}"""),
            Msg("tool_calls", $$$"""{"role":"assistant","content":null,"tool_calls":[{"id":"c2","type":"function","function":{"name":"run_query","arguments":"{\"sql\":\"{{{Sql}}}\",\"purpose\":\"contare\"}"}}]}"""),
            Msg("stop", """{"role":"assistant","content":"Ci sono 8.937 movimenti."}"""));
        var options = new LlmOptions { Provider = "openai", Model = "gpt-test", BaseUrl = "http://llm.local/v1" };

        var (entries, tools) = await Run(new OpenAiCompatibleChatModel(new HttpClient(api), options));

        AssertScenario(entries, tools);
        Assert.Equal("http://llm.local/v1/chat/completions", api.Requests[0].Url);
        using var second = JsonDocument.Parse(api.Requests[1].Body);
        var messages = second.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("c1", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("function", second.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Ollama_provider_uses_native_chat_api_with_tools_and_context_window()
    {
        static string Msg(string message) => "{\"model\":\"qwen2.5:7b\",\"done\":true,\"done_reason\":\"stop\",\"message\":" + message + "}";
        var api = new FakeApi(
            Msg("""{"role":"assistant","content":"","tool_calls":[{"function":{"name":"list_tables","arguments":{"filter":"mov"}}}]}"""),
            Msg($$$$"""{"role":"assistant","content":"","tool_calls":[{"function":{"name":"run_query","arguments":{"sql":"{{{{Sql}}}}","purpose":"contare"}}}]}"""),
            Msg("""{"role":"assistant","content":"Ci sono 8.937 movimenti."}"""));
        var options = new LlmOptions { Provider = "ollama", Model = "qwen2.5:7b", BaseUrl = "http://ollama:11434", ContextWindow = 32768 };

        var (entries, tools) = await Run(new OllamaChatModel(new HttpClient(api), options));

        AssertScenario(entries, tools);
        Assert.Equal("http://ollama:11434/api/chat", api.Requests[0].Url);
        using var second = JsonDocument.Parse(api.Requests[1].Body);
        Assert.False(second.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(32768, second.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        var messages = second.RootElement.GetProperty("messages");
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("list_tables", messages[3].GetProperty("tool_name").GetString());
        Assert.Equal("mov", messages[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetProperty("filter").GetString());
    }

    [Fact]
    public async Task Models_without_native_tools_use_the_json_protocol()
    {
        static string Msg(string content) => "{\"model\":\"m\",\"done\":true,\"message\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(content, Json.Default.String) + "}}";
        var api = new FakeApi(
            Msg("""{"tool":"list_tables","arguments":{"filter":"mov"}}"""),
            Msg($$$"""```json {"tool":"run_query","arguments":{"sql":"{{{Sql}}}","purpose":"contare"}} ```"""),
            Msg("""{"final":"Ci sono 8.937 movimenti."}"""));
        var options = new LlmOptions { Provider = "ollama", Model = "phi3", SupportsTools = false };

        var (entries, tools) = await Run(new OllamaChatModel(new HttpClient(api), options));

        AssertScenario(entries, tools);
        using var first = JsonDocument.Parse(api.Requests[0].Body);
        Assert.Equal("json", first.RootElement.GetProperty("format").GetString());
        Assert.False(first.RootElement.TryGetProperty("tools", out _));
        Assert.Contains("PROTOCOLLO STRUMENTI", first.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        using var third = JsonDocument.Parse(api.Requests[2].Body);
        Assert.Contains("Risultato di run_query", third.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString());
    }

    [Fact]
    public void History_is_replayed_as_text_including_the_queries_that_were_run()
    {
        var now = DateTimeOffset.UtcNow;
        ConversationEntry[] history =
        [
            new(1, EntryKind.User, "Quanti movimenti?", null, null, null, false, now),
            new(2, EntryKind.ToolCall, null, "run_query", $$$"""{"sql":"{{{Sql}}}","purpose":"p"}""", "N\n8937\n1 righe, 0.01s", false, now),
            new(3, EntryKind.Assistant, "Sono 8.937.", null, null, null, false, now)
        ];

        var messages = LlmDataAgent.Rebuild(history);

        Assert.Equal(2, messages.Count);
        Assert.Contains(Sql, messages[1].Text);
        Assert.Contains("Sono 8.937.", messages[1].Text);
    }
}

/// <summary>Contesto JSON source-generated per i test (serializzare una stringa senza reflection).</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class Json : System.Text.Json.Serialization.JsonSerializerContext;

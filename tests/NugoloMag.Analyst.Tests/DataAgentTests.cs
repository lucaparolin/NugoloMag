using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using NugoloMag.Analyst.Application.Assistant;
using NugoloMag.Analyst.Domain.Assistant;
using NugoloMag.Analyst.Infrastructure.Claude;

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

public class ClaudeDataAgentTests
{
    /// <summary>Finta API Messages: restituisce risposte preparate e registra le richieste.</summary>
    private sealed class FakeMessagesApi(params string[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[_next++], Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RecordingToolbox : IDataAgentToolbox
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
            return Task.FromResult(name == "list_tables"
                ? new ToolOutcome("dbo.MovMag\t8.937 righe", false)
                : new ToolOutcome("N\n8937\n1 righe, 0.01s", false));
        }
    }

    private static string Message(string id, string stopReason, string content) =>
        "{\"id\":\"" + id + "\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5\",\"content\":" + content +
        ",\"stop_reason\":\"" + stopReason + "\",\"stop_sequence\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}";

    private static readonly AgentContext Context = new("erp", "GestionaleDemo", "Movimenti in dbo.MovMag.", new DateOnly(2026, 9, 24));

    [Fact]
    public async Task Agent_uses_tools_autonomously_and_returns_the_answer_with_a_full_transcript()
    {
        var api = new FakeMessagesApi(
            Message("m1", "tool_use", """[{"type":"text","text":"Cerco la tabella dei movimenti."},{"type":"tool_use","id":"toolu_1","name":"list_tables","input":{"filter":"mov"}}]"""),
            Message("m2", "tool_use", """[{"type":"tool_use","id":"toolu_2","name":"run_query","input":{"sql":"SELECT COUNT(*) AS N FROM dbo.MovMag","purpose":"contare i movimenti"}}]"""),
            Message("m3", "end_turn", """[{"type":"text","text":"Ci sono 8.937 movimenti."}]"""));
        var client = new AnthropicClient { ApiKey = "test", HttpClient = new HttpClient(api), MaxRetries = 0 };
        var agent = new ClaudeDataAgent(client);
        var tools = new RecordingToolbox();

        var entries = await agent.ReplyAsync(Context, [], "Quanti movimenti ci sono?", tools);

        Assert.Equal(["list_tables", "run_query"], tools.Calls.Select(c => c.Name));
        Assert.Contains("SELECT COUNT(*)", tools.Calls[1].Input);
        Assert.Equal([EntryKind.Assistant, EntryKind.ToolCall, EntryKind.ToolCall, EntryKind.Assistant], entries.Select(e => e.Kind));
        Assert.Equal("Ci sono 8.937 movimenti.", entries[^1].Text);

        Assert.Equal(3, api.Requests.Count);
        using var second = JsonDocument.Parse(api.Requests[1]);
        var messages = second.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength()); // domanda, tool_use, tool_result
        Assert.Contains("\"tool_result\"", messages[2].GetRawText());
        Assert.Contains("toolu_1", messages[2].GetRawText());
        Assert.Contains("GestionaleDemo", second.RootElement.GetProperty("system").GetRawText());
        Assert.Equal(2, second.RootElement.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task Previous_turns_are_replayed_as_text_including_the_queries_that_were_run()
    {
        var api = new FakeMessagesApi(Message("m1", "end_turn", """[{"type":"text","text":"Nel magazzino MI01."}]"""));
        var client = new AnthropicClient { ApiKey = "test", HttpClient = new HttpClient(api), MaxRetries = 0 };
        var now = DateTimeOffset.UtcNow;
        ConversationEntry[] history =
        [
            new(1, EntryKind.User, "Quanti movimenti?", null, null, null, false, now),
            new(2, EntryKind.ToolCall, null, "run_query", """{"sql":"SELECT COUNT(*) FROM dbo.MovMag","purpose":"p"}""", "N\n8937\n1 righe, 0.01s", false, now),
            new(3, EntryKind.Assistant, "Sono 8.937.", null, null, null, false, now)
        ];

        await new ClaudeDataAgent(client).ReplyAsync(Context, history, "E dove sono di più?", new RecordingToolbox());

        using var request = JsonDocument.Parse(api.Requests[0]);
        var messages = request.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Contains("SELECT COUNT(*) FROM dbo.MovMag", messages[1].GetRawText());
        Assert.Contains("Sono 8.937.", messages[1].GetRawText());
    }
}

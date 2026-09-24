using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NugoloMag.Analyst.Application.Llm;
using NugoloMag.Analyst.Infrastructure.Llm;

namespace NugoloMag.Analyst.Tests;

/// <summary>Skill reali (cartella agents/ copiata nell'output) e un catalogo con un solo connettore di prova.</summary>
internal static class TestLlm
{
    public static readonly string SkillsRoot = Path.Combine(AppContext.BaseDirectory, "agents");
    public static readonly FileSkillLibrary Skills = new(SkillsRoot);

    public static AgentLlm For(IChatModel model) => new(Skills, new SingleConnector(model));

    private sealed class SingleConnector(IChatModel model) : IConnectorCatalog
    {
        public string? DefaultConnector => "test";
        public IReadOnlyList<string> Names { get; } = ["test"];
        public IChatModel? Get(string name) => model;
    }
}

public class SkillLibraryTests
{
    [Fact]
    public void Every_agent_has_its_skill_and_no_prompt_lives_in_code()
    {
        Assert.Empty(TestLlm.Skills.Validate([.. SkillIds.All, SkillIds.ConnectorCheck]));

        var orchestrator = TestLlm.Skills.Get(SkillIds.Orchestrator);
        Assert.Equal(8, orchestrator.MaxRounds(15));
        Assert.Contains("Prove prima delle conclusioni", orchestrator.Instructions); // incluso da _shared/principi.md
        Assert.DoesNotContain("{{>", orchestrator.Instructions);
        Assert.Equal(["narrative", "question"], TestLlm.Skills.Get(SkillIds.ReportNarrator).Prompts.Keys.Order());
        Assert.Contains("{{tools}}", TestLlm.Skills.Protocol(SkillIds.ToolEmulationProtocol));
    }

    [Fact]
    public void Placeholders_are_rendered_and_missing_values_become_empty()
    {
        var text = SkillTemplate.Render("Sorgente: {{source}}. {{ discovery_brief }}", new Dictionary<string, string?> { ["source"] = "erp" });
        Assert.Equal("Sorgente: erp.", text);
    }

    [Fact]
    public void Front_matter_is_parsed_and_files_are_reloaded_when_they_change()
    {
        var root = Directory.CreateTempSubdirectory("skills").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "_shared"));
            Directory.CreateDirectory(Path.Combine(root, "demo"));
            File.WriteAllText(Path.Combine(root, "_shared", "regole.md"), "Regola comune.");
            var skillFile = Path.Combine(root, "demo", "SKILL.md");
            File.WriteAllText(skillFile, "---\nname: Demo\nconnector: locale   # commento\nmax_tokens: 900\n---\nVersione 1. {{> _shared/regole.md}}");
            var library = new FileSkillLibrary(root);

            var v1 = library.Get("demo");
            Assert.Equal("Demo", v1.Name);
            Assert.Equal("locale", v1.Connector);
            Assert.Equal(900, v1.MaxTokens(4000));
            Assert.Equal("Versione 1. Regola comune.", v1.Instructions);

            File.WriteAllText(skillFile, "---\nname: Demo\n---\nVersione 2.");
            File.SetLastWriteTimeUtc(skillFile, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal("Versione 2.", library.Get("demo").Instructions);
            Assert.Null(library.Get("demo").Connector);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Includes_cannot_escape_the_skills_folder()
    {
        var root = Directory.CreateTempSubdirectory("skills").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "demo"));
            File.WriteAllText(Path.Combine(root, "demo", "SKILL.md"), "Testo {{> ../../etc/passwd}}");
            Assert.Throws<SkillException>(() => new FileSkillLibrary(root).Get("demo"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public class ConnectorConfigurationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void Named_connectors_default_and_agent_routes_are_bound()
    {
        var settings = LlmConfiguration.Read(Config(
            ("Llm:Default", "locale"),
            ("Llm:Connectors:locale:Provider", "ollama"),
            ("Llm:Connectors:locale:Model", "qwen2.5:3b"),
            ("Llm:Connectors:locale:SupportsTools", "false"),
            ("Llm:Connectors:azure:Provider", "azure"),
            ("Llm:Connectors:azure:Model", "gpt-4o"),
            ("Llm:Connectors:azure:BaseUrl", "https://res.openai.azure.com"),
            ("Llm:Connectors:azure:Temperature", ""),
            ("Llm:Connectors:azure:Headers:X-Team", "logistica"),
            ("Llm:Agents:orchestrator", "azure"),
            ("Llm:Agents:briefing", "")));

        Assert.Equal("locale", settings.Default);
        Assert.False(settings.Connectors["locale"].SupportsTools);
        Assert.Equal("locale", settings.Connectors["LOCALE"].Name);
        Assert.Null(settings.Connectors["azure"].Temperature);
        Assert.Equal("logistica", settings.Connectors["azure"].Headers["X-Team"]);
        Assert.Empty(LlmConfiguration.Validate(settings));

        var llm = new AgentLlm(TestLlm.Skills, new LlmConnectorRegistry(settings), settings.Agents);
        Assert.Equal("azure", llm.ConnectorFor(SkillIds.Orchestrator));
        Assert.Equal("locale", llm.ConnectorFor(SkillIds.Briefing));
        Assert.Equal("ollama", llm.Resolve(SkillIds.DataAssistant)!.Model.Info.Provider);
        Assert.Empty(llm.Validate(SkillIds.All));
    }

    [Fact]
    public void Legacy_flat_section_becomes_the_default_connector()
    {
        var settings = LlmConfiguration.Read(Config(("Llm:Provider", "ollama"), ("Llm:Model", "qwen2.5:7b"), ("Llm:ContextWindow", "8192")));
        Assert.Equal("default", settings.Default);
        Assert.Equal(8192, settings.Connectors["default"].ContextWindow);
    }

    [Fact]
    public void Wrong_routes_and_providers_are_reported_before_any_call()
    {
        var settings = LlmConfiguration.Read(Config(
            ("Llm:Default", "manca"),
            ("Llm:Connectors:x:Provider", "boh"),
            ("Llm:Connectors:x:Model", "m"),
            ("Llm:Agents:orchestrator", "fantasma")));
        var problems = LlmConfiguration.Validate(settings);
        Assert.Equal(3, problems.Count);

        var llm = new AgentLlm(TestLlm.Skills, new LlmConnectorRegistry(settings), new Dictionary<string, string> { ["sconosciuto"] = "x" });
        Assert.Contains(llm.Validate(SkillIds.All), p => p.Contains("sconosciuto"));
    }

    [Fact]
    public void Agents_routed_to_none_fall_back_to_deterministic_behaviour()
    {
        var settings = LlmConfiguration.Read(Config(("Llm:Connectors:locale:Provider", "ollama"), ("Llm:Connectors:locale:Model", "m"), ("Llm:Agents:briefing", "none")));
        var llm = new AgentLlm(TestLlm.Skills, new LlmConnectorRegistry(settings), settings.Agents);
        Assert.Null(llm.Resolve(SkillIds.Briefing));
        Assert.NotNull(llm.Resolve(SkillIds.Orchestrator));
    }
}

public class ConnectorProtocolTests
{
    private sealed class CapturingApi(string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private const string Completion = """{"choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}]}""";

    [Fact]
    public async Task Azure_uses_deployment_url_api_key_header_and_completion_tokens()
    {
        Environment.SetEnvironmentVariable("NUGOLO_TEST_AZURE_KEY", "segreta");
        var api = new CapturingApi(Completion);
        var options = new LlmOptions
        {
            Name = "azure", Provider = "azure", Model = "gpt-4o", Deployment = "prod-4o", BaseUrl = "https://res.openai.azure.com/",
            ApiVersion = "2024-10-21", ApiKeyEnvironmentVariable = "NUGOLO_TEST_AZURE_KEY", TokenParameter = "max_completion_tokens", Temperature = null,
            Headers = new Dictionary<string, string> { ["X-Team"] = "logistica" }
        };

        await new OpenAiCompatibleChatModel(new HttpClient(api), options).CompleteAsync(new ChatRequest("s", [ChatMessage.User("u")], [], 100));

        Assert.Equal("https://res.openai.azure.com/openai/deployments/prod-4o/chat/completions?api-version=2024-10-21", api.Request!.RequestUri!.ToString());
        Assert.Equal("segreta", api.Request.Headers.GetValues("api-key").Single());
        Assert.Null(api.Request.Headers.Authorization);
        Assert.Equal("logistica", api.Request.Headers.GetValues("X-Team").Single());
        using var body = JsonDocument.Parse(api.Body);
        Assert.Equal(100, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task Servers_without_json_mode_are_not_asked_for_it()
    {
        var api = new CapturingApi(Completion);
        var options = new LlmOptions { Provider = "openai", Model = "m", BaseUrl = "http://localhost:1234/v1", SupportsJsonMode = false };
        await new OpenAiCompatibleChatModel(new HttpClient(api), options).CompleteAsync(new ChatRequest("s", [ChatMessage.User("u")], [], JsonOnly: true));
        using var body = JsonDocument.Parse(api.Body);
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
    }
}

public class ConnectorDiagnosticsTests
{
    /// <summary>Finto Ollama: catalogo modelli + risposte in sequenza alle prove.</summary>
    private sealed class FakeOllama(params string[] chatContents) : HttpMessageHandler
    {
        private int _next;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.RequestUri!.AbsolutePath == "/api/tags"
                ? """{"models":[{"name":"qwen2.5:3b"}]}"""
                : chatContents[_next++];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static string Msg(string content) =>
        "{\"done\":true,\"message\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(content, Json.Default.String) + "}}";

    [Fact]
    public async Task All_checks_pass_for_a_model_that_follows_the_emulated_tool_protocol()
    {
        var api = new FakeOllama(
            Msg("pronto"),
            Msg("""{"articolo":"ART-042","giacenza":137,"sotto_scorta":false}"""),
            Msg("""{"tool":"giacenza","arguments":{"articolo":"ART-042"}}"""),
            Msg("""{"final":"Ci sono 137 pezzi."}"""));
        var options = new LlmOptions { Name = "locale", Provider = "ollama", Model = "qwen2.5:3b", BaseUrl = "http://ollama:11434", SupportsTools = false };
        var http = new HttpClient(api);

        var report = await new LlmDiagnostics(TestLlm.Skills, http).RunAsync(options, new OllamaChatModel(http, options));

        Assert.True(report.IsUsable);
        Assert.All(report.Checks, c => Assert.Equal(CheckStatus.Passed, c.Status));
        Assert.Equal(5, report.Checks.Count);
    }

    [Fact]
    public async Task Missing_key_stops_the_checks_before_calling_the_model()
    {
        var options = new LlmOptions { Name = "openai", Provider = "openai", Model = "gpt-5", ApiKeyEnvironmentVariable = "NUGOLO_TEST_KEY_NON_IMPOSTATA" };
        var report = await new LlmDiagnostics(TestLlm.Skills).RunAsync(options, null);
        Assert.False(report.IsUsable);
        Assert.Contains("NUGOLO_TEST_KEY_NON_IMPOSTATA", report.Checks.Single().Detail);
    }

    [Fact]
    public async Task A_model_that_ignores_the_tool_is_reported()
    {
        var api = new FakeOllama(Msg("pronto"), Msg("""{"articolo":"ART-042","giacenza":137}"""), Msg("Credo siano circa 100."));
        var options = new LlmOptions { Provider = "ollama", Model = "qwen2.5:3b", BaseUrl = "http://ollama:11434" };
        var http = new HttpClient(api);

        var report = await new LlmDiagnostics(TestLlm.Skills, http).RunAsync(options, new OllamaChatModel(http, options));

        var tools = report.Checks.Last();
        Assert.Equal(CheckStatus.Failed, tools.Status);
        Assert.Contains("SupportsTools=false", tools.Detail);
    }

    /// <summary>Collaudo reale: impostare NUGOLO_TEST_OLLAMA=http://localhost:11434 e NUGOLO_TEST_OLLAMA_MODEL (default qwen2.5:3b).</summary>
    [Fact]
    public async Task Live_ollama_connector_when_available()
    {
        if (Environment.GetEnvironmentVariable("NUGOLO_TEST_OLLAMA") is not { Length: > 0 } url) return;
        var options = new LlmOptions
        {
            Name = "live", Provider = "ollama", Model = Environment.GetEnvironmentVariable("NUGOLO_TEST_OLLAMA_MODEL") ?? "qwen2.5:3b",
            BaseUrl = url, ContextWindow = 4096, TimeoutSeconds = 900
        };
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };

        var report = await new LlmDiagnostics(TestLlm.Skills).RunAsync(options, new OllamaChatModel(http, options));

        Assert.True(report.IsUsable, string.Join("\n", report.Checks.Select(c => $"{c.Status} {c.Name}: {c.Detail}")));
    }
}

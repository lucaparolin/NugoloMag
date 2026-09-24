using NugoloMag.Analyst.Application.Agentic;
using NugoloMag.Analyst.Domain;
using NugoloMag.Analyst.Domain.Agentic;
using NugoloMag.Analyst.Infrastructure.Demo;
using Xunit.Abstractions;
using static NugoloMag.Analyst.Tests.Builders;

namespace NugoloMag.Analyst.Tests;

public class AgentScenarioTests(ITestOutputHelper output)
{
    private static AgentWorkspace DemoWorkspace()
    {
        var rows = new SyntheticInventoryGenerator().Generate(AsOf, days: 60);
        var tasks = new SyntheticTaskGenerator().Generate(rows, AsOf).Select(t => t.Task);
        return new AgentWorkspace("demo", Window, new InventoryDataset(rows), new TaskDataset(tasks), DateTimeOffset.UtcNow, []);
    }

    [Fact]
    public void Productivity_agent_finds_the_zone_c_relocation_and_exonerates_the_mix_driven_drop()
    {
        var ws = DemoWorkspace();
        var findings = new ProductivityAgent(new AgenticSettings()).Observe(ws);
        foreach (var f in findings)
        {
            output.WriteLine($"{f.Signature} | {f.Title} | sev {SeverityModel.Score(f.Severity)} {f.Urgency}");
            foreach (var e in f.Evidence) output.WriteLine($"   [{e.Kind}] {e.Statement}");
        }

        var mi = Assert.Single(findings, f => f.Signature == "productivity:below-expectation:MI01:PICK");
        Assert.Contains(mi.Evidence, e => e.Statement.Contains($"{SyntheticTaskGenerator.RelocatedSkus} articoli hanno cambiato zona"));
        Assert.Contains(mi.Hypotheses, h => h.Code == "relocation");
        Assert.Contains(mi.Hypotheses, h => h.Code == "workload" && h.Status == HypothesisStatus.Rejected);

        Assert.Contains(findings, f => f.Signature == "productivity:mix-driven:RM02:PICK");
        Assert.DoesNotContain(findings, f => f.Warehouse == "NA03");
        Assert.DoesNotContain(findings, f => f.Signature.EndsWith(":PACK"));
    }
}

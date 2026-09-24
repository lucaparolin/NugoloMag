using NugoloMag.Analyst.Application.Agentic;
using NugoloMag.Analyst.Domain.Agentic;

namespace NugoloMag.Analyst.Tests;

public class AgenticRulesTests
{
    private static Incident Spike(string warehouse, string? cause) => new()
    {
        Id = 1, SourceName = "s", Warehouse = warehouse, Domain = OperationalDomain.Inventory, Signature = $"inventory:spike:{warehouse}:outbound",
        Title = "Picco", DetectedAt = DateTimeOffset.UtcNow, FirstObserved = new DateOnly(2026, 9, 23), UpdatedAt = DateTimeOffset.UtcNow,
        Status = IncidentStatus.Open, Severity = SeverityLevel.Medium, SeverityScore = 0.6, Confidence = ConfidenceLevel.High, Scope = "",
        Signals = [], ProbableRootCause = cause
    };

    [Fact]
    public void Same_pattern_with_different_local_causes_is_not_systemic()
    {
        var mi = Spike("MI01", "Evento sulla categoria Elettronica");
        var na = Spike("NA03", "Evento sulla categoria Bevande");
        Assert.Equal(0, IncidentManager.Escalation(mi, [mi, na], new AgenticSettings()).Level);
    }

    [Fact]
    public void Same_pattern_without_a_local_explanation_is_escalated_as_systemic()
    {
        var mi = Spike("MI01", null);
        var na = Spike("NA03", null);
        var (level, reason) = IncidentManager.Escalation(mi, [mi, na], new AgenticSettings());
        Assert.Equal(2, level);
        Assert.Contains("più magazzini", reason);
    }

    [Theory]
    [InlineData(1.0, 1.0, 1.0, 1.0, SeverityLevel.Critical)]
    [InlineData(0.9, 0.4, 0.1, 0.3, SeverityLevel.Low)]
    [InlineData(0.0, 1.0, 1.0, 1.0, SeverityLevel.Informational)]
    public void Severity_is_multiplicative(double magnitude, double scope, double impact, double urgency, SeverityLevel expected) =>
        Assert.Equal(expected, SeverityModel.Level(new SeverityInputs(magnitude, scope, impact, urgency, ConfidenceLevel.High)));

    [Theory]
    [InlineData(2, 0, 0, ConfidenceLevel.High)]
    [InlineData(1, 0, 1, ConfidenceLevel.Medium)]
    [InlineData(0, 1, 1, ConfidenceLevel.Low)]
    public void Confidence_follows_independent_evidence(int support, int contra, int alternatives, ConfidenceLevel expected) =>
        Assert.Equal(expected, ConfidenceModel.Assess(support, contra, alternatives));
}

public class ImpactRulesTests
{
    private static AgentFinding Adjustment(double baseline, double observed) => new()
    {
        Agent = "inventory", Domain = OperationalDomain.Inventory, Signature = "inventory:drop:NA03:adjustment", Warehouse = "NA03",
        Title = "t", Observation = "o", WhyItMatters = "w", Evidence = [], Baseline = "b", Deviation = "d", Scope = "s",
        Confidence = ConfidenceLevel.High, Severity = new(1, 0.7, 0.6, 0.5, ConfidenceLevel.High), Intensity = 1,
        Measures = new Dictionary<string, double> { ["baseline"] = baseline, ["observed"] = observed, ["value_exposed"] = 18000 }
    };

    private static readonly InvestigationResult None = new([], null, [], [], ConfidenceLevel.Low, [], [], [], null, null, []);

    [Fact]
    public void Only_negative_adjustments_count_as_money_at_risk()
    {
        var impact = new ImpactAgent(new AgenticSettings());
        Assert.Contains(impact.Estimate(Adjustment(0, -600), None), i => i.Unit == "€");
        Assert.DoesNotContain(impact.Estimate(Adjustment(0, 600), None), i => i.Unit == "€");
    }
}

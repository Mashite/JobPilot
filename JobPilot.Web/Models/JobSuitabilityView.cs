namespace JobPilot.Web.Models;

public sealed record JobSuitabilityView(int AreaCoverage, IReadOnlyList<AreaCoverageView> Areas,
    IReadOnlyList<string> CriticalGaps, IReadOnlyList<string> ImportantGaps, IReadOnlyList<string> NeedingClarification,
    IReadOnlyList<string> Transferable);

public sealed record AreaCoverageView(string Name, int Coverage, int RequirementCount);

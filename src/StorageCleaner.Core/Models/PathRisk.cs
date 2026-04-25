namespace StorageCleaner.Core.Models;

public sealed record PathRisk(
    PathRiskLevel Level,
    string Reason)
{
    public bool IsRisky => Level >= PathRiskLevel.Caution;

    public bool RequiresExplicitOverride => Level >= PathRiskLevel.HighRisk;

    public bool IsProtected => Level == PathRiskLevel.Protected;
}

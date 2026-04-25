using StorageCleaner.Core.Models;

namespace StorageCleaner.App.ViewModels;

internal static class SafetyScoreCalculator
{
    public static int FromRisk(PathRisk risk)
    {
        return risk.Level switch
        {
            PathRiskLevel.Safe => 96,
            PathRiskLevel.Caution => 78,
            PathRiskLevel.HighRisk => 36,
            PathRiskLevel.Protected => 4,
            _ => 50
        };
    }

    public static string Recommendation(PathRisk risk, bool preferred)
    {
        if (risk.IsProtected)
        {
            return "Blocked";
        }

        if (risk.RequiresExplicitOverride)
        {
            return "Needs Review";
        }

        return preferred ? "Recommended" : "Review";
    }
}
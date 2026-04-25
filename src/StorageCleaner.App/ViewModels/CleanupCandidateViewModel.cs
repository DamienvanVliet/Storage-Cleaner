using CommunityToolkit.Mvvm.ComponentModel;
using StorageCleaner.Core.Models;

namespace StorageCleaner.App.ViewModels;

public partial class CleanupCandidateViewModel : ObservableObject
{
    public CleanupCandidateViewModel(CleanupCandidate candidate, bool isSelected = true)
    {
        Candidate = candidate;
        IsSelected = isSelected;
    }

    public CleanupCandidate Candidate { get; }

    public string FullPath => Candidate.FullPath;

    public string Name => Candidate.DisplayName;

    public CleanupCategory Category => Candidate.Category;

    public long SizeBytes => Candidate.SizeBytes;

    public DateTime LastModifiedUtc => Candidate.LastModifiedUtc;

    public bool IsDirectory => Candidate.IsDirectory;

    public string RiskLabel => Candidate.Risk.Level.ToString();

    public string RiskReason => Candidate.Risk.Reason;

    public int SafetyScore => SafetyScoreCalculator.FromRisk(Candidate.Risk);

    public bool RequiresExplicitOverride => Candidate.Risk.RequiresExplicitOverride;

    public bool IsRecommended => !Candidate.Risk.RequiresExplicitOverride && !Candidate.Risk.IsProtected;

    public string SafetyRecommendation => IsRecommended ? "Recommended" : "Needs Review";

    [ObservableProperty]
    private bool isSelected = true;
}

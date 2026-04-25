namespace StorageCleaner.App.Models;

public sealed class FeatureTileModel
{
    public FeatureTileModel(
        string title,
        string description,
        string iconGlyph,
        NavigationSection? targetSection,
        bool isAvailable,
        string destinationHint)
    {
        Title = title;
        Description = description;
        IconGlyph = iconGlyph;
        TargetSection = targetSection;
        IsAvailable = isAvailable;
        DestinationHint = destinationHint;
    }

    public string Title { get; }

    public string Description { get; }

    public string IconGlyph { get; }

    public NavigationSection? TargetSection { get; }

    public bool IsAvailable { get; }

    public string DestinationHint { get; }
}

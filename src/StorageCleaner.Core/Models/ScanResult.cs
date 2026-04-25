namespace StorageCleaner.Core.Models;

public sealed class ScanResult
{
    public required IReadOnlyList<FolderNode> Roots { get; init; }

    public required IReadOnlyList<ScanIssue> Issues { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required long TotalScannedBytes { get; init; }

    public required long TotalFiles { get; init; }

    public required long TotalFolders { get; init; }

    public TimeSpan Duration => CompletedAt - StartedAt;

    public IReadOnlyList<FolderNode> FlattenedFolders => _flattenedFolders ??= FlattenFolders();

    private IReadOnlyList<FolderNode>? _flattenedFolders;

    private IReadOnlyList<FolderNode> FlattenFolders()
    {
        var result = new List<FolderNode>(TotalFolders > int.MaxValue ? int.MaxValue : (int)Math.Max(TotalFolders, 16));
        var stack = new Stack<FolderNode>(Roots.Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            result.Add(node);
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }

        return result;
    }
}

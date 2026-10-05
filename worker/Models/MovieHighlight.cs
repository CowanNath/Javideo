using System.Text.Json.Serialization;

namespace Javideo.Worker.Models;

public sealed class MovieHighlight
{
    public long Id { get; set; }
    public long MovieId { get; set; }
    public string? Title { get; set; }
    public string? Note { get; set; }
    public int? StartSeconds { get; set; }
    public int? EndSeconds { get; set; }
    public string? SourceFileName { get; set; }
    public List<MovieHighlightAsset> Assets { get; set; } = new();
}

public sealed class MovieHighlightAsset
{
    public long Id { get; set; }
    public long HighlightId { get; set; }
    public string Kind { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string Url { get; set; } = "";
    [JsonIgnore] public string StorageName { get; set; } = "";
    [JsonIgnore] public string ContentType { get; set; } = "";
}

public sealed class SaveHighlightRequest
{
    public string? Title { get; set; }
    public string? Note { get; set; }
    public int? StartSeconds { get; set; }
    public int? EndSeconds { get; set; }
    public string? SourceFileName { get; set; }
    public List<long> KeepAssetIds { get; set; } = new();
}

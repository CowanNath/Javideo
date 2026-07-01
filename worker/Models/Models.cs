namespace Javideo.Worker.Models;

public class Library
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string MetadataSource { get; set; } = "metatube";
    public List<string> Directories { get; set; } = new();
    public long MovieCount { get; set; }
}

public class Movie
{
    public long Id { get; set; }
    public long? LibraryId { get; set; }
    public string Number { get; set; } = "";
    public string? Title { get; set; }
    public string? OriginalTitle { get; set; }
    public string? Summary { get; set; }
    public string? Maker { get; set; }
    public string? Label { get; set; }
    public string? Series { get; set; }
    public string? Director { get; set; }
    public string? ReleaseDate { get; set; }
    public int? RuntimeMinutes { get; set; }
    public string? CoverUrl { get; set; }
    public string? ThumbUrl { get; set; }
    public double? Score { get; set; }
    public string? Provider { get; set; }
    public string? HomepageUrl { get; set; }
    public string? FolderPath { get; set; }
    public List<Actor> Actors { get; set; } = new();
    public List<Tag> Tags { get; set; } = new();
    public List<MagnetResult> Magnets { get; set; } = new();
    public List<string> PreviewImages { get; set; } = new();
    public bool HasTrailer { get; set; }
}

public class Actor
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public long MovieCount { get; set; }
}

public class Tag
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "genre";
    public bool IsStandard { get; set; } = true;
    public long MovieCount { get; set; }
}

public class MagnetResult
{
    public string Title { get; set; } = "";
    public string Size { get; set; } = "";
    public string MagnetUri { get; set; } = "";
    public string Source { get; set; } = "";
}

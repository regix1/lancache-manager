namespace LancacheManager.Models;

/// <summary>
/// Response for directory listing
/// </summary>
public class DirectoryListResponse
{
    public string CurrentPath { get; set; } = string.Empty;
    public string? ParentPath { get; set; }
    public List<FileSystemItemDto> Items { get; set; } = new();
}

/// <summary>
/// File system item DTO
/// </summary>
public class FileSystemItemDto
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public bool IsAccessible { get; set; } = true;
}

/// <summary>
/// Response for file search results
/// </summary>
public class FileSearchResponse
{
    public string SearchPath { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public List<FileSystemItemDto> Results { get; set; } = new();
}

using LancacheManager.Data;
using LancacheManager.Infrastructure.Repositories.Interfaces;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Repositories;

public class TagsRepository : ITagsRepository
{
    private readonly AppDbContext _context;
    private readonly ILogger<TagsRepository> _logger;

    public TagsRepository(AppDbContext context, ILogger<TagsRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<List<Tag>> GetAllTagsAsync(CancellationToken cancellationToken = default)
    {
        var tags = await _context.Tags
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);

        foreach (var tag in tags)
        {
            tag.CreatedAtUtc = DateTime.SpecifyKind(tag.CreatedAtUtc, DateTimeKind.Utc);
        }

        return tags;
    }

    public async Task<Tag?> GetTagByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var tag = await _context.Tags
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (tag != null)
        {
            tag.CreatedAtUtc = DateTime.SpecifyKind(tag.CreatedAtUtc, DateTimeKind.Utc);
        }

        return tag;
    }

    public async Task<Tag?> GetTagByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var tag = await _context.Tags
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Name.ToLower() == name.ToLower(), cancellationToken);

        if (tag != null)
        {
            tag.CreatedAtUtc = DateTime.SpecifyKind(tag.CreatedAtUtc, DateTimeKind.Utc);
        }

        return tag;
    }

    public async Task<Tag> CreateTagAsync(Tag tag, CancellationToken cancellationToken = default)
    {
        tag.CreatedAtUtc = DateTime.UtcNow;
        _context.Tags.Add(tag);
        await _context.SaveChangesAsync(cancellationToken);

        tag.CreatedAtUtc = DateTime.SpecifyKind(tag.CreatedAtUtc, DateTimeKind.Utc);

        _logger.LogInformation("Created tag: {Name} (ID: {Id})", tag.Name, tag.Id);
        return tag;
    }

    public async Task<Tag> UpdateTagAsync(Tag tag, CancellationToken cancellationToken = default)
    {
        var existing = await _context.Tags.FindAsync(new object[] { tag.Id }, cancellationToken);
        if (existing == null)
        {
            throw new InvalidOperationException($"Tag with ID {tag.Id} not found");
        }

        existing.Name = tag.Name;
        existing.ColorIndex = tag.ColorIndex;
        existing.Description = tag.Description;

        await _context.SaveChangesAsync(cancellationToken);

        existing.CreatedAtUtc = DateTime.SpecifyKind(existing.CreatedAtUtc, DateTimeKind.Utc);

        _logger.LogInformation("Updated tag: {Name} (ID: {Id})", existing.Name, existing.Id);
        return existing;
    }

    public async Task DeleteTagAsync(int id, CancellationToken cancellationToken = default)
    {
        var tag = await _context.Tags.FindAsync(new object[] { id }, cancellationToken);
        if (tag != null)
        {
            _context.Tags.Remove(tag);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Deleted tag: {Name} (ID: {Id})", tag.Name, tag.Id);
        }
    }

    public async Task<List<Download>> GetDownloadsWithTagAsync(int tagId, CancellationToken cancellationToken = default)
    {
        var downloads = await _context.DownloadTags
            .AsNoTracking()
            .Where(dt => dt.TagId == tagId)
            .Select(dt => dt.Download)
            .OrderByDescending(d => d.StartTimeUtc)
            .ToListAsync(cancellationToken);

        foreach (var download in downloads)
        {
            download.StartTimeUtc = DateTime.SpecifyKind(download.StartTimeUtc, DateTimeKind.Utc);
            if (download.EndTimeUtc != default)
            {
                download.EndTimeUtc = DateTime.SpecifyKind(download.EndTimeUtc, DateTimeKind.Utc);
            }
        }

        return downloads;
    }

    public async Task<List<Tag>> GetTagsForDownloadAsync(int downloadId, CancellationToken cancellationToken = default)
    {
        var tags = await _context.DownloadTags
            .AsNoTracking()
            .Where(dt => dt.DownloadId == downloadId)
            .Select(dt => dt.Tag)
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);

        foreach (var tag in tags)
        {
            tag.CreatedAtUtc = DateTime.SpecifyKind(tag.CreatedAtUtc, DateTimeKind.Utc);
        }

        return tags;
    }

    public async Task<int> GetTagUsageCountAsync(int tagId, CancellationToken cancellationToken = default)
    {
        return await _context.DownloadTags
            .CountAsync(dt => dt.TagId == tagId, cancellationToken);
    }
}

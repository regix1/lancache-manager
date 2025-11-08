using LancacheManager.Application.Services.Blizzard.Extensions;

namespace LancacheManager.Application.Services.Blizzard;

/// <summary>
/// Maps chunk data (archive + offset) to game files using real Blizzard CDN data.
/// </summary>
public class ChunkMapper
{
    private readonly Dictionary<ChunkLocation, GameFileInfo> _chunkToFileMap;
    private readonly CDNClient _cdnClient;
    private readonly ILogger? _logger;

    public ChunkMapper(CDNClient cdnClient, ILogger? logger = null)
    {
        _chunkToFileMap = new Dictionary<ChunkLocation, GameFileInfo>();
        _cdnClient = cdnClient;
        _logger = logger;
    }

    /// <summary>
    /// Builds the chunk-to-file mapping for a Blizzard product.
    /// This downloads and parses all necessary manifests from the CDN.
    /// </summary>
    /// <param name="product">Product code (e.g., "wow", "s1", "pro")</param>
    /// <param name="languageFilter">Optional language filter (e.g., "enUS"). Null/empty = no filter.</param>
    /// <param name="platformFilter">Optional platform filter (e.g., "Windows"). Null/empty = no filter.</param>
    public async Task BuildMappingAsync(string product, string? languageFilter = null, string? platformFilter = null)
    {
        _logger?.LogInformation("Building Chunk Mapping for {Product}", product.ToUpper());

        // Step 1: Get version information
        _logger?.LogInformation("Step 1: Getting version information...");
        var versionsContent = await _cdnClient.GetVersionsAsync(product);
        var version = Parsers.ParseVersions(versionsContent);
        _logger?.LogInformation("BuildConfig: {BuildConfig}, CDNConfig: {CDNConfig}", version.buildConfig, version.cdnConfig);

        // Step 2: Download and parse build config
        _logger?.LogInformation("Step 2: Downloading build config...");
        var buildConfigData = await _cdnClient.DownloadConfigAsync(version.buildConfig.ToMD5());
        var buildConfig = Parsers.ParseBuildConfig(System.Text.Encoding.UTF8.GetString(buildConfigData));
        _logger?.LogInformation("Build: {BuildName}, Install manifest: {InstallManifest}", buildConfig.buildName, buildConfig.install[1]);

        // Step 3: Download and parse CDN config
        _logger?.LogInformation("Step 3: Downloading CDN config...");
        var cdnConfigData = await _cdnClient.DownloadConfigAsync(version.cdnConfig.ToMD5());
        var cdnConfig = Parsers.ParseCDNConfig(System.Text.Encoding.UTF8.GetString(cdnConfigData));
        _logger?.LogInformation("Archives: {ArchiveCount}", cdnConfig.archives.Count);

        // Step 4: Download and parse install manifest
        _logger?.LogInformation("Step 4: Downloading install manifest...");
        var installData = await _cdnClient.DownloadDataAsync(buildConfig.install[1]);
        var installFile = Parsers.ParseInstallFile(installData);
        _logger?.LogInformation("Total files: {TotalFiles}", installFile.numEntries);

        // Filter by language and platform
        var filteredEntries = installFile.entries.AsEnumerable();

        // Log sample tags to help debug filtering
        if (installFile.numEntries > 0)
        {
            var sampleTags = installFile.entries.Take(5).SelectMany(e => e.tags).Distinct().Take(20);
            _logger?.LogInformation("Sample tags from install manifest: {Tags}", string.Join(", ", sampleTags));
        }

        if (!string.IsNullOrEmpty(languageFilter))
        {
            filteredEntries = filteredEntries.Where(e => e.tags.Any(t => t.Contains(languageFilter, StringComparison.OrdinalIgnoreCase)));
            _logger?.LogInformation("Applied language filter: {Filter}", languageFilter);
        }
        if (!string.IsNullOrEmpty(platformFilter))
        {
            filteredEntries = filteredEntries.Where(e => e.tags.Any(t => t.Contains(platformFilter, StringComparison.OrdinalIgnoreCase)));
            _logger?.LogInformation("Applied platform filter: {Filter}", platformFilter);
        }
        var filteredList = filteredEntries.ToList();
        _logger?.LogInformation("Filtered files: {FilteredCount} (from {TotalCount} total)", filteredList.Count, installFile.numEntries);

        // Step 5: Download and parse encoding table
        _logger?.LogInformation("Step 5: Downloading encoding table...");
        _logger?.LogDebug("Using encoding index: {EncodingHash}", buildConfig.encoding[1].ToHexString());
        var encodingData = await _cdnClient.DownloadDataAsync(buildConfig.encoding[1]);
        _logger?.LogDebug("Downloaded encoding file: {Size} bytes", encodingData.Length);
        var encodingTable = Parsers.ParseEncodingFile(encodingData, _logger);
        _logger?.LogInformation("Encoding entries: {EncodingCount}", encodingTable.ReversedEncodingDictionary.Count);

        // Step 6: Download and parse archive indexes
        _logger?.LogInformation("Step 6: Downloading {ArchiveCount} archive indexes...", cdnConfig.archives.Count);
        var combinedArchiveIndex = new Dictionary<MD5Hash, ArchiveIndexEntry>();
        int successfulArchives = 0;
        int failedArchives = 0;

        for (int i = 0; i < cdnConfig.archives.Count; i++)
        {
            try
            {
                var archiveIndexData = await _cdnClient.DownloadDataAsync(cdnConfig.archives[i].hashIdMd5, isIndex: true);
                var archiveIndex = Parsers.ParseArchiveIndex(archiveIndexData, i);

                foreach (var kvp in archiveIndex)
                {
                    combinedArchiveIndex.TryAdd(kvp.Key, kvp.Value);
                }
                successfulArchives++;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to download archive index {Index}", i);
                failedArchives++;
            }
        }
        _logger?.LogInformation("Successfully downloaded: {Success}/{Total} archives", successfulArchives, cdnConfig.archives.Count);
        if (failedArchives > 0)
        {
            _logger?.LogWarning("Failed archives: {Failed} (these chunks will not be mapped)", failedArchives);
        }
        _logger?.LogInformation("Total archive entries: {TotalEntries}", combinedArchiveIndex.Count);

        // Step 7: Build the mapping
        _logger?.LogInformation("Step 7: Building chunk-to-file mapping from {FileCount} filtered files...", filteredList.Count);

        // Log sample content hashes for debugging
        if (filteredList.Count > 0)
        {
            var sampleHashes = filteredList.Take(3).Select(e => e.contentHash.ToHexString());
            _logger?.LogDebug("Sample content hashes from install manifest: {Hashes}", string.Join(", ", sampleHashes));

            var sampleEncodingKeys = encodingTable.ReversedEncodingDictionary.Keys.Take(3).Select(k => k.ToHexString());
            _logger?.LogDebug("Sample content keys from encoding table: {Keys}", string.Join(", ", sampleEncodingKeys));
        }

        int mapped = 0;
        int skippedNoEncoding = 0;
        int skippedNoArchive = 0;

        foreach (var entry in filteredList)
        {
            // Look up through encoding table
            if (!encodingTable.ReversedEncodingDictionary.TryGetValue(entry.contentHash, out MD5Hash upperHash))
            {
                skippedNoEncoding++;
                if (skippedNoEncoding <= 3)  // Log first few failures for debugging
                {
                    _logger?.LogDebug("File {FileName} not found in encoding table (hash: {Hash})", entry.name, entry.contentHash.ToHexString());
                }
                continue;
            }

            // Look up in archive indexes
            if (!combinedArchiveIndex.TryGetValue(upperHash, out ArchiveIndexEntry location))
            {
                skippedNoArchive++;
                if (skippedNoArchive <= 3)  // Log first few failures for debugging
                {
                    _logger?.LogDebug("File {FileName} CDN key not found in archive indexes (key: {Key})", entry.name, upperHash.ToHexString());
                }
                continue;
            }

            var chunkLoc = new ChunkLocation(location.Index, location.Offset);

            var fileInfo = new GameFileInfo
            {
                FileName = entry.name,
                Size = location.Size,
                ContentHash = entry.contentHash,
                Location = chunkLoc,
                Tags = entry.tags
            };

            _chunkToFileMap[chunkLoc] = fileInfo;
            mapped++;

            // Log first few successful mappings for verification
            if (mapped <= 3)
            {
                _logger?.LogDebug("Mapped file {FileName} to archive {Archive} offset {Offset}", entry.name, location.Index, location.Offset);
            }
        }

        _logger?.LogInformation("Mapping complete: {Mapped} successful, {NoEncoding} skipped (no encoding), {NoArchive} skipped (no archive)",
            mapped, skippedNoEncoding, skippedNoArchive);

        if (mapped == 0 && filteredList.Count > 0)
        {
            _logger?.LogWarning("WARNING: All {Count} filtered files failed to map. This suggests a problem with the encoding table or archive indexes.", filteredList.Count);
        }
    }

    /// <summary>
    /// Finds the file that a specific chunk belongs to.
    /// </summary>
    public GameFileInfo? FindFile(int archiveIndex, uint byteOffset)
    {
        if (archiveIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(archiveIndex), "Archive index cannot be negative");
        }

        var location = new ChunkLocation(archiveIndex, byteOffset);
        return _chunkToFileMap.TryGetValue(location, out var info) ? info : null;
    }

    /// <summary>
    /// Finds all files that start within a byte range in an archive.
    /// </summary>
    public List<GameFileInfo> FindFilesInRange(int archiveIndex, uint startOffset, uint endOffset)
    {
        if (archiveIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(archiveIndex), "Archive index cannot be negative");
        }
        if (endOffset < startOffset)
        {
            throw new ArgumentException("End offset must be greater than or equal to start offset");
        }

        return _chunkToFileMap
            .Where(kvp =>
                kvp.Key.ArchiveIndex == archiveIndex &&
                kvp.Key.ByteOffset >= startOffset &&
                kvp.Key.ByteOffset <= endOffset)
            .Select(kvp => kvp.Value)
            .ToList();
    }

    /// <summary>
    /// Gets all mapped files.
    /// </summary>
    public IEnumerable<GameFileInfo> GetAllFiles()
    {
        return _chunkToFileMap.Values;
    }

    /// <summary>
    /// Searches for files by name pattern.
    /// </summary>
    public List<GameFileInfo> FindFilesByName(string searchPattern)
    {
        if (string.IsNullOrWhiteSpace(searchPattern))
        {
            throw new ArgumentException("Search pattern cannot be null or empty", nameof(searchPattern));
        }

        return _chunkToFileMap.Values
            .Where(f => f.FileName.Contains(searchPattern, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Gets the count of mapped chunks
    /// </summary>
    public int GetMappingCount()
    {
        return _chunkToFileMap.Count;
    }

    /// <summary>
    /// Clears all mapped chunks
    /// </summary>
    public void ClearMapping()
    {
        _chunkToFileMap.Clear();
    }
}

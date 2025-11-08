using LancacheManager.Application.Services.Blizzard.Extensions;
using System.Collections;
using System.Text;

namespace LancacheManager.Application.Services.Blizzard;

public static class Parsers
{
    /// <summary>
    /// Parses the versions file to get the latest version entry
    /// </summary>
    public static VersionsEntry ParseVersions(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            throw new Exception("Invalid versions file - not enough lines");
        }

        // Find the header line - headers may include type info after ! (e.g., "BuildConfig!HEX:16")
        var headers = lines[0].Split('|').Select(h => h.Split('!')[0]).ToArray();
        var buildConfigIdx = Array.IndexOf(headers, "BuildConfig");
        var cdnConfigIdx = Array.IndexOf(headers, "CDNConfig");
        var productConfigIdx = Array.IndexOf(headers, "ProductConfig");

        // Validate that all required headers were found
        if (buildConfigIdx == -1)
        {
            throw new Exception($"Missing BuildConfig header in versions file. Headers: {string.Join(", ", headers)}");
        }
        if (cdnConfigIdx == -1)
        {
            throw new Exception($"Missing CDNConfig header in versions file. Headers: {string.Join(", ", headers)}");
        }
        if (productConfigIdx == -1)
        {
            throw new Exception($"Missing ProductConfig header in versions file. Headers: {string.Join(", ", headers)}");
        }

        // Parse the last (most recent) entry
        var lastLine = lines[lines.Length - 1];
        var values = lastLine.Split('|');

        // Validate that we have enough values
        if (values.Length <= buildConfigIdx || values.Length <= cdnConfigIdx || values.Length <= productConfigIdx)
        {
            throw new Exception($"Invalid versions file - not enough values in data row (expected at least {Math.Max(buildConfigIdx, Math.Max(cdnConfigIdx, productConfigIdx)) + 1}, got {values.Length}). Headers: {string.Join(", ", headers)}");
        }

        return new VersionsEntry
        {
            buildConfig = values[buildConfigIdx],
            cdnConfig = values[cdnConfigIdx],
            productConfig = values[productConfigIdx]
        };
    }

    /// <summary>
    /// Parses the build config file
    /// </summary>
    public static BuildConfigFile ParseBuildConfig(string content)
    {
        var buildConfig = new BuildConfigFile();

        var lines = content.Split("\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith('#') || line.Length == 0)
            {
                continue;
            }

            var cols = line.Split(" = ", StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 2)
            {
                continue;
            }

            switch (cols[0])
            {
                case "download":
                    buildConfig.download = cols[1].Split(' ').Select(e => e.ToMD5()).ToArray();
                    break;
                case "install":
                    buildConfig.install = cols[1].Split(' ').Select(e => e.ToMD5()).ToArray();
                    break;
                case "encoding":
                    buildConfig.encoding = cols[1].Split(' ').Select(e => e.ToMD5()).ToArray();
                    break;
                case "encoding-size":
                    buildConfig.encodingSize = cols[1].Split(' ').Select(e => int.Parse(e)).ToArray();
                    break;
                case "install-size":
                    buildConfig.installSize = cols[1].Split(' ').Select(e => int.Parse(e)).ToArray();
                    break;
                case "build-name":
                    buildConfig.buildName = cols[1];
                    break;
            }
        }

        return buildConfig;
    }

    /// <summary>
    /// Parses the CDN config file
    /// </summary>
    public static CDNConfigFile ParseCDNConfig(string content)
    {
        var cdnConfig = new CDNConfigFile();

        var lines = content.Split("\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith('#') || line.Length == 0)
            {
                continue;
            }

            var cols = line.Split(" = ", StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 2)
            {
                continue;
            }

            switch (cols[0])
            {
                case "archives":
                    var archiveHashes = cols[1].Split(' ');
                    foreach (var hash in archiveHashes)
                    {
                        cdnConfig.archives.Add(new Archive
                        {
                            hashId = hash,
                            hashIdMd5 = hash.ToMD5()
                        });
                    }
                    break;
                case "file-index":
                    cdnConfig.fileIndex = cols[1].ToMD5();
                    break;
            }
        }

        return cdnConfig;
    }

    /// <summary>
    /// Parses an install manifest file (BLTE compressed)
    /// </summary>
    public static InstallFile ParseInstallFile(byte[] content)
    {
        var install = new InstallFile();

        using var memoryStream = BLTE.Parse(content);
        using BinaryReader bin = new BinaryReader(memoryStream);

        if (Encoding.UTF8.GetString(bin.ReadBytes(2)) != "IN")
        {
            throw new Exception("Error while parsing install file. Not a valid install manifest.");
        }

        bin.ReadByte();

        install.hashSize = bin.ReadByte();
        if (install.hashSize != 16)
        {
            throw new Exception("Unsupported install hash size!");
        }

        install.numTags = bin.ReadUInt16BigEndian();
        install.numEntries = bin.ReadUInt32BigEndian();

        int bytesPerTag = ((int)install.numEntries + 7) / 8;

        install.tags = new InstallTagEntry[install.numTags];

        for (var i = 0; i < install.numTags; i++)
        {
            install.tags[i].name = bin.ReadCString();
            install.tags[i].type = bin.ReadUInt16BigEndian();

            var filebits = bin.ReadBytes(bytesPerTag);

            for (int j = 0; j < bytesPerTag; j++)
            {
                filebits[j] = (byte)((filebits[j] * 0x0202020202 & 0x010884422010) % 1023);
            }

            install.tags[i].files = new BitArray(filebits);
        }

        install.entries = new InstallFileEntry[install.numEntries];

        byte[] md5HashBuffer = BinaryReaderExtensions.AllocateBuffer<MD5Hash>();

        for (var i = 0; i < install.numEntries; i++)
        {
            install.entries[i].name = bin.ReadCString();
            install.entries[i].contentHash = bin.ReadMd5Hash(md5HashBuffer);
            install.entries[i].size = bin.ReadUInt32BigEndian();
            install.entries[i].tags = new List<string>();
            for (var j = 0; j < install.numTags; j++)
            {
                if (install.tags[j].files[i] == true)
                {
                    install.entries[i].tags.Add(install.tags[j].type + "=" + install.tags[j].name);
                }
            }
        }

        return install;
    }

    /// <summary>
    /// Parses an archive index file
    /// </summary>
    public static Dictionary<MD5Hash, ArchiveIndexEntry> ParseArchiveIndex(byte[] content, int archiveIndex)
    {
        var indexDict = new Dictionary<MD5Hash, ArchiveIndexEntry>();

        // Check if file is large enough to have a footer
        if (content.Length < 28)
        {
            throw new Exception($"Archive index file too small ({content.Length} bytes, need at least 28)");
        }

        using var stream = new MemoryStream(content);
        using var bin = new BinaryReader(stream);

        const int CHUNK_SIZE = 4096;

        // Read footer
        stream.Seek(-28, SeekOrigin.End);
        bin.ReadBytes(8); // tocHash
        bin.ReadBytes(8); // version
        bin.ReadByte(); // unk0
        bin.ReadByte(); // unk1
        bin.ReadByte(); // unk2
        byte blockSizeKB = bin.ReadByte();
        byte offsetBytes = bin.ReadByte();
        byte sizeBytes = bin.ReadByte();
        byte keySizeInBytes = bin.ReadByte();
        byte checksumSize = bin.ReadByte();
        uint numElements = bin.ReadUInt32BigEndian();

        // Reset to start
        stream.Seek(0, SeekOrigin.Begin);

        byte[] md5Buffer = BinaryReaderExtensions.AllocateBuffer<MD5Hash>();

        for (int j = 0; j < numElements; j++)
        {
            // Check if we have enough bytes for this entry (16 for hash + 4 for size + 4 for offset)
            if (stream.Position + 24 > stream.Length)
            {
                // Not enough data, stop parsing
                break;
            }

            MD5Hash key = bin.ReadMd5Hash(md5Buffer);
            var indexEntry = new ArchiveIndexEntry(
                (short)archiveIndex,
                bin.ReadUInt32BigEndian(),  // size
                bin.ReadUInt32BigEndian()   // offset
            );

            indexDict.TryAdd(key, indexEntry);

            // Skip to next chunk boundary
            long remaining = CHUNK_SIZE - (stream.Position % CHUNK_SIZE);
            if (remaining < 16 + 4 + 4)  // Not enough space for next record
            {
                // Make sure we don't seek past the end
                if (stream.Position + remaining <= stream.Length)
                {
                    stream.Position += remaining;
                }
            }
        }

        return indexDict;
    }

    /// <summary>
    /// Parses an encoding table (BLTE compressed)
    /// </summary>
    public static EncodingFile ParseEncodingFile(byte[] content, ILogger? logger = null)
    {
        var encoding = new EncodingFile();

        using var memoryStream = BLTE.Parse(content);
        using BinaryReader bin = new BinaryReader(memoryStream);

        if (Encoding.UTF8.GetString(bin.ReadBytes(2)) != "EN")
        {
            throw new Exception("Error while parsing encoding file.");
        }

        bin.ReadByte();  // version
        byte hashSizeCKey = bin.ReadByte();
        byte hashSizeEKey = bin.ReadByte();
        ushort cKeyPageSize = bin.ReadUInt16BigEndian();
        ushort eKeyPageSize = bin.ReadUInt16BigEndian();
        uint cKeyPageCount = bin.ReadUInt32BigEndian();
        uint eKeyPageCount = bin.ReadUInt32BigEndian();
        bin.ReadByte();  // unk
        uint stringBlockSize = bin.ReadUInt32BigEndian();

        encoding.numEntriesA = cKeyPageCount * cKeyPageSize;

        logger?.LogDebug("Encoding file header: hashSizeCKey={CKeySize}, hashSizeEKey={EKeySize}, cKeyPageSize={PageSize}, cKeyPageCount={PageCount}, expectedEntries={Expected}, stringBlockSize={StringBlockSize}, streamLength={Length}",
            hashSizeCKey, hashSizeEKey, cKeyPageSize, cKeyPageCount, encoding.numEntriesA, stringBlockSize, memoryStream.Length);

        // Skip the string block (comes after header, before page data)
        bin.BaseStream.Position += stringBlockSize;
        logger?.LogDebug("Skipped string block of {Size} bytes, now at position {Position}", stringBlockSize, bin.BaseStream.Position);

        // Mark the start position for page alignment calculations
        long start = bin.BaseStream.Position;

        encoding.ReversedEncodingDictionary = new Dictionary<MD5Hash, MD5Hash>();

        int parsedEntries = 0;
        int pagesProcessed = 0;
        bool hitBoundsCheck = false;

        for (int i = 0; i < cKeyPageCount; i++)
        {
            // Check if we have enough data for the page header
            if (bin.BaseStream.Position + 16 > bin.BaseStream.Length)
            {
                logger?.LogWarning("Hit bounds check at page {Page}/{TotalPages}: not enough data for page header (pos={Pos}, len={Len})",
                    i, cKeyPageCount, bin.BaseStream.Position, bin.BaseStream.Length);
                hitBoundsCheck = true;
                break;
            }

            bin.ReadBytes(hashSizeCKey);  // firstHash (page header) - use actual hash size
            pagesProcessed++;

            byte keysCount;
            int entriesInPage = 0;
            while (bin.BaseStream.Position + 1 <= bin.BaseStream.Length && (keysCount = bin.ReadByte()) != 0)
            {
                // Log first few keysCount values for debugging
                if (i < 3 && entriesInPage < 2)
                {
                    logger?.LogDebug("Page {Page}, entry {Entry}: keysCount={KeysCount}", i, entriesInPage, keysCount);
                }

                // Check if we have enough data for this entry
                // Format: 1 byte keyCount + 5 bytes file_size + hashSizeCKey bytes + (keysCount * hashSizeEKey) bytes
                long requiredBytes = 5 + hashSizeCKey + (keysCount * hashSizeEKey);
                if (bin.BaseStream.Position + requiredBytes > bin.BaseStream.Length)
                {
                    logger?.LogWarning("Hit bounds check in page {Page}: keysCount={KeysCount}, need={Need}, avail={Avail}",
                        i, keysCount, requiredBytes, bin.BaseStream.Length - bin.BaseStream.Position);
                    hitBoundsCheck = true;
                    break;
                }

                bin.BaseStream.Position += 5;  // Skip file_size (40-bit / 5 bytes)

                // Read content key (CKey) - variable size based on header
                byte[] ckeyBytes = bin.ReadBytes(hashSizeCKey);
                var contentKey = hashSizeCKey == 16 ? MD5Hash.FromBytes(ckeyBytes) : throw new Exception($"Unsupported CKey hash size: {hashSizeCKey}");

                // Read encoding key (EKey) - variable size based on header
                byte[] ekeyBytes = bin.ReadBytes(hashSizeEKey);
                var encodingKey = hashSizeEKey == 16 ? MD5Hash.FromBytes(ekeyBytes) : throw new Exception($"Unsupported EKey hash size: {hashSizeEKey}");

                // Skip additional encoding keys if present
                bin.BaseStream.Position += (keysCount - 1) * hashSizeEKey;

                encoding.ReversedEncodingDictionary.TryAdd(contentKey, encodingKey);
                parsedEntries++;
                entriesInPage++;
            }

            // Align to next 4KB chunk
            var remaining = 4096 - ((bin.BaseStream.Position - start) % 4096);
            if (remaining > 0 && remaining < 4096 && bin.BaseStream.Position + remaining <= bin.BaseStream.Length)
            {
                bin.BaseStream.Position += remaining;
            }
        }

        logger?.LogDebug("Encoding parsing complete: pages={Pages}/{TotalPages}, entries={Entries}/{Expected}, hitBoundsCheck={HitBounds}",
            pagesProcessed, cKeyPageCount, parsedEntries, encoding.numEntriesA, hitBoundsCheck);

        if (parsedEntries < encoding.numEntriesA / 2)
        {
            logger?.LogWarning("Only parsed {Parsed} entries out of expected {Expected} - encoding table may be incomplete",
                parsedEntries, encoding.numEntriesA);
        }

        return encoding;
    }
}

namespace LancacheManager.Core.Services;

/// <summary>
/// Shared knowledge about lancache access-log source layouts. Mirrors the Rust
/// log_layout module: a directory can hold the monolithic access.log series, the
/// bare-metal per-service *-access.log series (steam-access.log, ...), or both.
/// The filename-to-service map here must stay in lockstep with the Rust side.
/// </summary>
public static class LogSourceLayout
{
    public const string MonolithicStem = "access.log";
    public const string FallbackStem = "fallback-access.log";

    public const string LayoutMonolithic = "monolithic";
    public const string LayoutBareMetal = "bare_metal";
    public const string LayoutMixed = "mixed";

    /// <summary>
    /// Filename prefixes (before -access.log) that bare-metal actually writes: the five
    /// per-service vhosts plus the special fallback file. CLOSED set, in lockstep with the
    /// Rust log_layout BARE_METAL_SOURCE_PREFIXES. A directory can hold other *-access.log
    /// files that are NOT lancache cache logs (most commonly the nginx stream module's
    /// stream-access.log beside a monolithic access.log); treating those as per-service
    /// sources would misread a monolithic datasource as bare-metal/mixed.
    /// </summary>
    private static readonly HashSet<string> _bareMetalSourcePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "epicgames", "blizzard", "riot", "windows-update", "fallback"
    };

    /// <summary>
    /// Candidate per-service stems for a manager service name, used to clear positions
    /// after a service-scoped removal. This is the REVERSE of the Rust-side filename-hint
    /// map (log_layout.rs service_for_prefix) — the forward map lives only in Rust, which
    /// is the sole component that attributes records. "wsus" owns both its own literal
    /// stem and the bare-metal "windows-update" spelling.
    /// </summary>
    public static IReadOnlyList<string> StemsForService(string service)
    {
        var lower = service.ToLowerInvariant();
        // "fallback" is not a manager service; never synthesize its stem, or a removal
        // request using that token would clear the fallback series checkpoint while the
        // files survive.
        if (lower == "fallback")
        {
            return Array.Empty<string>();
        }
        return lower == "wsus"
            ? new[] { "wsus-access.log", "windows-update-access.log" }
            : new[] { $"{lower}-access.log" };
    }

    /// <summary>
    /// Derive the logical stem for a file name, stripping .gz/.zst and numeric rotation
    /// suffixes. Returns null when the name is not an access-log series member
    /// (nginx-error.log and stream logs are never access-log evidence).
    /// </summary>
    public static string? LogicalStem(string fileName)
    {
        // Ordinal (case-sensitive) on purpose: the Rust discovery strips exactly ".gz"
        // and ".zst", and both sides must agree on what belongs to a series.
        var withoutCompression = fileName;
        var isCompressed = false;
        if (withoutCompression.EndsWith(".gz", StringComparison.Ordinal))
        {
            withoutCompression = withoutCompression[..^3];
            isCompressed = true;
        }
        else if (withoutCompression.EndsWith(".zst", StringComparison.Ordinal))
        {
            withoutCompression = withoutCompression[..^4];
            isCompressed = true;
        }

        var baseName = withoutCompression;
        var hasRotation = false;
        var lastDot = withoutCompression.LastIndexOf('.');
        if (lastDot > 0)
        {
            var suffix = withoutCompression[(lastDot + 1)..];
            if (suffix.Length > 0 && suffix.All(char.IsAsciiDigit))
            {
                baseName = withoutCompression[..lastDot];
                hasRotation = true;
            }
        }

        // Discovery only replays a compressed file when it is a numbered rotation, because
        // a compression-only name (access.log.gz) cannot join the live source's file series.
        // Rejecting it here keeps this side from claiming a layout, or halting the logs/http
        // descent, on a file the record processor would ignore.
        if (isCompressed && !hasRotation)
        {
            return null;
        }

        if (baseName == MonolithicStem)
        {
            return baseName;
        }
        if (baseName.EndsWith("-access.log", StringComparison.Ordinal) &&
            baseName.Length > "-access.log".Length)
        {
            // Only recognized bare-metal source names count. A stray *-access.log (e.g. the
            // nginx stream module's stream-access.log) must NOT become a per-service source.
            var prefix = baseName[..^"-access.log".Length];
            if (_bareMetalSourcePrefixes.Contains(prefix))
            {
                return baseName;
            }
        }
        return null;
    }

    /// <summary>True when the stem is a per-service (bare-metal) source, fallback included.</summary>
    public static bool IsPerServiceStem(string stem) => stem != MonolithicStem;

    /// <summary>
    /// Enumerate the logical source stems present in a directory (no descent).
    /// Returns an empty set when the directory is missing or unreadable.
    /// </summary>
    public static HashSet<string> EnumerateStems(string directory)
    {
        var stems = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(directory))
            {
                return stems;
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var stem = LogicalStem(name);
                if (stem != null)
                {
                    stems.Add(stem);
                }
            }
        }
        catch
        {
            // Unreadable directory: treated as no sources; callers surface the warning.
        }
        return stems;
    }

    /// <summary>
    /// Resolve the directory that actually holds the access-log sources. Accepts the
    /// given directory when it has sources directly; descends into its "http" child ONLY
    /// when that child carries the bare-metal per-service topology. This closes the trap
    /// where the bare-metal parent logs/ (holding only nginx-error.log, with HTTP logs in
    /// logs/http/) validated but then produced zero-work "success".
    /// </summary>
    public static string ResolveAccessLogDirectory(string directory)
    {
        var stems = EnumerateStems(directory);
        if (stems.Count > 0)
        {
            return directory;
        }

        var httpDir = Path.Combine(directory, "http");
        if (Directory.Exists(httpDir))
        {
            var httpStems = EnumerateStems(httpDir);
            if (httpStems.Any(IsPerServiceStem))
            {
                return httpDir;
            }
        }

        return directory;
    }

    /// <summary>Presentation-only layout label for a stem set. Never drives capability.</summary>
    public static string DeriveLayout(IReadOnlyCollection<string> stems)
    {
        var hasMonolithic = stems.Contains(MonolithicStem);
        var hasBareMetal = stems.Any(IsPerServiceStem);
        if (hasMonolithic && hasBareMetal)
        {
            return LayoutMixed;
        }
        return hasBareMetal ? LayoutBareMetal : LayoutMonolithic;
    }

    /// <summary>
    /// All current (non-rotated) source files for a directory, one per stem present.
    /// </summary>
    public static List<string> CurrentSourceFiles(string directory)
    {
        return EnumerateStems(directory)
            .OrderBy(s => s, StringComparer.Ordinal)
            .Select(stem => Path.Combine(directory, stem))
            .Where(File.Exists)
            .ToList();
    }
}

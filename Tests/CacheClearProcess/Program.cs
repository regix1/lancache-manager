using System.Text.Json;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 6 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1])
                || args[2] is not ("preserve" or "full" or "rsync") || args[3] != "--progress"
                || args[4] != "--operation-id" || !Guid.TryParse(args[5], out var operationId))
                throw new ArgumentException("Expected cache path, progress path, delete mode, --progress and --operation-id");
            var cachePath = new DirectoryInfo(args[0]).FullName;
            var cacheFilesExist = Directory
                .EnumerateFiles(cachePath, "*", SearchOption.AllDirectories)
                .Any(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _));
            if (cacheFilesExist)
            {
                var receiptPath = Path.Combine(
                    cachePath,
                    $".lancache-repair-{operationId:N}.json");
                await using var receipt = new FileStream(
                    receiptPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await JsonSerializer.SerializeAsync(receipt, new
                {
                    version = 1,
                    operationId,
                    cachePath,
                    hadCacheFiles = true
                });
                await receipt.FlushAsync();
                receipt.Flush(flushToDisk: true);
            }

            return await ProcessPipe.RunAsync("cache-clear", args, args[1], "cache-clear-pipe",
                "Commanded cache clear process failure");
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Cache clear process failed: {exception}");
            return 1;
        }
    }
}

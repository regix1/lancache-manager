internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 4 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1])
                || args[2] is not ("preserve" or "full" or "rsync") || args[3] != "--progress")
                throw new ArgumentException("Expected cache path, progress path, delete mode and --progress");
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

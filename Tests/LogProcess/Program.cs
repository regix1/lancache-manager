internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 5
                || args.Any(string.IsNullOrWhiteSpace)
                || args[2] is not ("0" or "1"))
            {
                throw new ArgumentException(
                    "Expected log directory, progress path, auto-map flag, datasource and positions path");
            }

            return await ProcessPipe.RunAsync(
                "log-processing",
                args,
                args[1],
                "log-processing-pipe",
                "Commanded log processing failure");
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Log process failed: {exception}");
            return 1;
        }
    }
}

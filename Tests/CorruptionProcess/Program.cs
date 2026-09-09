internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException("A supported corruption command is required");
            var command = args[0];
            var progressIndex = command switch
            {
                "remove" => 4,
                "remove-structural" or "structural-summary" => 2,
                "summary" => 3,
                _ => throw new ArgumentException("A supported corruption command is required")
            };
            var optionIndex = command == "summary" ? 6 : progressIndex + 1;
            if (args.Length < optionIndex || args.Take(optionIndex).Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Required corruption arguments were missing");
            var reportMode = command is "summary" or "structural-summary";
            var required = command switch
            {
                "summary" => new HashSet<string> { "--lookback-days", "--scan-started-utc", "--key-scheme" },
                "structural-summary" => new HashSet<string> { "--scan-started-utc", "--scan-mode", "--state-scope", "--key-scheme" },
                _ => new HashSet<string> { "--evidence-file", "--progress", "--key-scheme" }
            };
            var seen = new HashSet<string>();
            string? evidencePath = null;
            for (var index = optionIndex; index < args.Length; index++)
            {
                var option = args[index];
                if ((!required.Contains(option) && !(command == "remove" && option == "--stem-positions")) || !seen.Add(option))
                    throw new ArgumentException("An unsupported or repeated corruption option was supplied");
                if (option == "--progress") continue;
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    throw new ArgumentException("A corruption option value was missing");
                var value = args[index];
                if (option == "--evidence-file") evidencePath = value;
                if (option == "--scan-mode" && value is not ("full" or "incremental"))
                    throw new ArgumentException("The structural scan mode was invalid");
                if (option == "--lookback-days" && (!int.TryParse(value, out var days) || days <= 0))
                    throw new ArgumentException("The corruption lookback was invalid");
            }
            if (!required.IsSubsetOf(seen)) throw new ArgumentException("A required corruption option was missing");
            if (command == "summary" && (!int.TryParse(args[5], out var threshold) || threshold <= 0))
                throw new ArgumentException("The corruption threshold was invalid");
            return await ProcessPipe.RunAsync(command, args, args[progressIndex], "corruption-pipe",
                "Commanded corruption process failure", reportMode, evidencePath);
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Corruption process failed: {exception}");
            return 1;
        }
    }
}

using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class ProcessPipe
{
    private sealed record Connection(string Command, string[] Arguments, string ProgressPath, int ProcessId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EvidencePath);

    private sealed record Step(JsonElement Checkpoint, int? ExitCode, JsonElement? Report = null);

    public static async Task<int> RunAsync(string command, string[] arguments, string progressPath,
        string controlFile, string failure, bool reportMode = false, string? evidencePath = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var directory = Path.GetDirectoryName(Path.GetFullPath(progressPath))!;
        var pipeName = (await File.ReadAllTextAsync(Path.Combine(directory, controlFile), timeout.Token)).Trim();
        if (pipeName.Length == 0) throw new InvalidDataException("The control pipe name was empty");
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var connection = new Connection(command, arguments, progressPath, Environment.ProcessId, evidencePath);
        await writer.WriteLineAsync(JsonSerializer.Serialize(connection, options).AsMemory(), timeout.Token);

        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token)
                ?? throw new IOException("The control pipe disconnected before an exit command");
            var step = JsonSerializer.Deserialize<Step>(line, options)
                ?? throw new InvalidDataException("The control command was empty");
            if (step.Checkpoint.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("A complete checkpoint object is required");
            var temporaryPath = progressPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, step.Checkpoint.GetRawText(), timeout.Token);
                File.Move(temporaryPath, progressPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }

            if (!reportMode)
            {
                var completed = step.Checkpoint.TryGetProperty("status", out var status) && status.GetString() == "completed";
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { @event = completed ? "complete" : "progress" }));
                await Console.Out.FlushAsync(timeout.Token);
            }
            if (step.ExitCode is not { } exitCode) continue;
            if (exitCode != 0)
            {
                await Console.Error.WriteLineAsync(failure);
                return exitCode;
            }
            if (reportMode)
            {
                if (step.Report is not { ValueKind: JsonValueKind.Object } report || !report.EnumerateObject().Any())
                    throw new InvalidDataException("A successful summary requires a complete report object");
                await Console.Out.WriteLineAsync(report.GetRawText());
                await Console.Out.FlushAsync(timeout.Token);
            }
            return 0;
        }
    }
}

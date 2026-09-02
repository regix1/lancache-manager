using System.Reflection;
using System.Text;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// What happens when state.json cannot be written. A save used to log and return, so an admin who
/// changed a setting was told 200 OK and found the setting gone after the next restart, with nothing
/// on screen ever having said otherwise.
///
/// The split asserted here is by who asked for the write. A setting change came from a person who is
/// waiting for an answer, so the failure travels out as <see cref="ServiceUnavailableException"/> and
/// <see cref="GlobalExceptionMiddleware"/> turns it into a 503 carrying the reason. The writes the
/// load path performs for itself - the legacy-file migration, the one-time migrations, the first-run
/// anchors - were asked for by nobody, and <see cref="StateService.GetState"/> backs every read in
/// the application, so a failure there is logged and the application keeps running.
///
/// The write is made to fail by putting a directory where the temp file goes, which is the same
/// failure an unwritable data directory produces and needs no special permissions to arrange.
/// </summary>
public sealed class StateSaveFailureTests : IDisposable
{
    private const string SaveFailedMessage =
        "The state file could not be written, so your change was not saved. Check the server logs and the permissions on the data directory.";

    private readonly string _root;

    public StateSaveFailureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-state-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SettingChange_WhenTheStateFileCannotBeWritten_ThrowsInsteadOfReportingSuccess()
    {
        BlockStateFileWrites();
        var service = CreateStateService();

        var thrown = Assert.Throws<ServiceUnavailableException>(() => service.SetTopGameCount(42));

        Assert.Equal(SaveFailedMessage, thrown.Message);
        Assert.Equal("errors.state.saveFailed", thrown.StageKey);
    }

    [Fact]
    public async Task SettingChange_WhenTheStateFileCannotBeWritten_Answers503WithTheReason()
    {
        BlockStateFileWrites();
        var service = CreateStateService();

        var thrown = Assert.Throws<ServiceUnavailableException>(() => service.SetTopGameCount(42));
        var response = await RunMiddlewareAsync(thrown);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Equal(SaveFailedMessage, response.Error);
        Assert.Equal("errors.state.saveFailed", response.StageKey);
    }

    /// <summary>
    /// Past the failure count that stops the service attempting the write at all. That lockout used
    /// to return as if the save had happened, which made every later setting change silently do
    /// nothing for the rest of the process's life. The blocker is removed before the last call, so
    /// the throw can only come from the lockout refusing to write, not from a write that failed.
    /// </summary>
    [Fact]
    public void SettingChange_AfterTheLockoutStopsAttemptingWrites_StillThrows()
    {
        BlockStateFileWrites();
        var service = CreateStateService();

        for (var attempt = 0; attempt < 8; attempt++)
        {
            Assert.Throws<ServiceUnavailableException>(() => service.SetTopGameCount(attempt + 1));
        }

        Directory.Delete(TempStateFilePath);

        var thrown = Assert.Throws<ServiceUnavailableException>(() => service.SetTopGameCount(99));

        Assert.Equal(SaveFailedMessage, thrown.Message);
        Assert.False(File.Exists(StateFilePath));
    }

    [Fact]
    public void Startup_WhenTheStateFileCannotBeWritten_LoadsInsteadOfFailing()
    {
        BlockStateFileWrites();
        var service = CreateStateService();

        // The load path writes twice here - the legacy-file migration result and the seeded first-run
        // anchors - and both fail. Reading a value proves the load itself completed.
        var state = service.GetState();

        Assert.NotNull(state);
        Assert.False(service.GetSetupCompleted());
    }

    private string StateDirectory => Path.Combine(_root, nameof(IPathResolver.GetStateDirectory));

    private string StateFilePath => Path.Combine(StateDirectory, "state.json");

    private string TempStateFilePath => StateFilePath + ".tmp";

    /// <summary>
    /// Occupies the temp file the save writes first with a directory of the same name, so the write
    /// fails the way it does on a data directory the container cannot write to.
    /// </summary>
    private void BlockStateFileWrites()
    {
        Directory.CreateDirectory(TempStateFilePath);
    }

    private StateService CreateStateService()
    {
        var configuration = new ConfigurationBuilder().Build();

        var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)pathResolver).Root = _root;

        var dataProtection = DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(_root, "dp-keys")));
        var apiKeyService = new ApiKeyService(
            NullLogger<ApiKeyService>.Instance,
            configuration,
            pathResolver);
        var encryption = new SecureStateEncryptionService(
            dataProtection,
            apiKeyService,
            NullLogger<SecureStateEncryptionService>.Instance);
        var steamAuthStorage = new SteamAuthStorageService(
            NullLogger<SteamAuthStorageService>.Instance,
            pathResolver,
            encryption);

        return new StateService(
            NullLogger<StateService>.Instance,
            pathResolver,
            encryption,
            steamAuthStorage);
    }

    /// <summary>
    /// Runs one request through <see cref="GlobalExceptionMiddleware"/> whose inner delegate throws
    /// <paramref name="thrown"/>. Production, because that is where the middleware replaces an
    /// exception message with a safe one - in Development every message is echoed back and the test
    /// would pass with or without the typed exception.
    /// </summary>
    private static async Task<MiddlewareReply> RunMiddlewareAsync(Exception thrown)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/metrics/game-limit";
        var body = new MemoryStream();
        context.Response.Body = body;

        var middleware = new GlobalExceptionMiddleware(
            _ => throw thrown,
            NullLogger<GlobalExceptionMiddleware>.Instance,
            new TestHostEnvironment("Production"));

        await middleware.InvokeAsync(context);

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(body.ToArray()));
        return new MiddlewareReply(
            context.Response.StatusCode,
            document.RootElement.GetProperty("error").GetString() ?? string.Empty,
            document.RootElement.TryGetProperty("stageKey", out var stageKey) ? stageKey.GetString() : null);
    }

    private sealed record MiddlewareReply(int StatusCode, string Error, string? StageKey);

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "LancacheManager";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

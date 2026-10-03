using System.Net;
using System.Reflection;
using System.Text;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A catalog refresh whose follow-up step failed still saved its games, so it ends completed with a
/// warning naming how many steps failed rather than a clean green run.
/// </summary>
public sealed class EpicCatalogRefreshWarningTests
{
    [Fact]
    public async Task AnEpicRefreshWhoseCdnStepFailedEndsAmberAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "epic-refresh-warning", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AuthCredentialFormatTests.TempDirPathResolver(root);
            var keys = new ApiKeyService(NullLogger<ApiKeyService>.Instance, new ConfigurationBuilder().Build(), paths);
            var encryption = new SecureStateEncryptionService(
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))), keys,
                NullLogger<SecureStateEncryptionService>.Instance);
            var storage = new EpicAuthStorageService(NullLogger<EpicAuthStorageService>.Instance, paths, encryption);
            storage.SaveAuthData(new EpicAuthData { RefreshToken = "refresh", DisplayName = "owner" });

            using var http = new HttpClient(new CdnStepFailsHandler());
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"epic_refresh_warning_{Guid.NewGuid():N}")
                .Options;
            var tracker = new UnifiedOperationTracker(
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<UnifiedOperationTracker>.Instance);
            using var services = new ServiceCollection().BuildServiceProvider();
            using var service = new EpicMappingService(
                NullLogger<EpicMappingService>.Instance,
                new EpicApiDirectClient(http, NullLogger<EpicApiDirectClient>.Instance),
                storage,
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                new TestDbContextFactory(options),
                tracker,
                services.GetRequiredService<IServiceScopeFactory>(),
                DispatchProxy.Create<IStateService, NullReturningProxy>());
            typeof(EpicMappingService)
                .GetField("_currentTokens", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, new EpicOAuthTokens
                {
                    AccessToken = "access",
                    RefreshToken = "refresh",
                    ExpiresAt = DateTime.UtcNow.AddHours(1),
                });

            using var source = new CancellationTokenSource();
            await using var reporter = new MappingOperationReporter(
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                tracker,
                MappingOperations.Epic,
                new RunNotice(NotificationMode.Manual, RunTrigger.Manual),
                source.Token,
                NullLogger.Instance);
            await reporter.StartAsync();

            await (Task)typeof(EpicMappingService)
                .GetMethod("RefreshCatalogAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [reporter, reporter.Token])!;

            var warning = Assert.Single(tracker.GetOperation(reporter.OperationId)!.Warnings);
            Assert.Equal("common.notifications.warnings.epicStepsFailed", warning.StageKey);
            Assert.Equal(1, warning.Context["count"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Answers the owned-assets request with no assets, then fails the next request the way a dropped
    /// connection does, which is the CDN step's own assets request.
    /// </summary>
    private sealed class CdnStepFailsHandler : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 2)
            {
                throw new HttpRequestException("connection dropped");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            });
        }
    }
}

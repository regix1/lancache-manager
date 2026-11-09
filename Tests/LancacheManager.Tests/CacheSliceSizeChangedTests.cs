using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

/// <summary>
/// A second tab, a second admin, or a page open while a script sends the PATCH shows the stored slice
/// size only when the save is announced.
/// </summary>
public sealed class CacheSliceSizeChangedTests : IDisposable
{
    private readonly string _root;

    public CacheSliceSizeChangedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-slice-size-changed-" + Guid.NewGuid().ToString("N"));
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
    public async Task SavingTheSliceSizeStoresItAndAnnouncesTheChange()
    {
        var stateService = CreateStateService(_root);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();
        var controller = new SystemController(
            stateService,
            new ConfigurationBuilder().Build(),
            NullLogger<SystemController>.Instance,
            pathResolver: null!,
            cacheClearingService: null!,
            steamKit2Service: null!,
            datasourceService: null!,
            notifications,
            userPreferencesService: null!,
            capabilityService: null!,
            nginxLogRotationService: null!,
            cacheManagementService: null!,
            dbContextFactory: null!,
            claimWindow: null!);

        // Read through object so the test also builds against a synchronous action.
        object returned = controller.SetCacheSliceSizeAsync(new CacheSliceSizeRequest { SliceSizeBytes = 8_388_608 });
        var result = returned is Task<IActionResult> saving ? await saving : (IActionResult)returned;

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(8_388_608, stateService.GetCacheSliceSizeBytes());
        var announced = Assert.Single(((RecordingNotifications)(object)notifications).Events);
        Assert.Equal("CacheSliceSizeChanged", announced.EventName);
        Assert.Null(announced.Payload);
    }
}

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Models.ApiRequests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

/// <summary>
/// A wizard step id lives in two places that cannot see each other: the <see cref="SetupStep"/> enum
/// with its two switch arms, and the <c>InitStep</c> union the client sends. Adding a step to one side
/// only is silent at compile time and only shows up at runtime, because
/// <c>SetupStepJsonConverter.Read</c> maps an unrecognised string to <see cref="SetupStep.Unknown"/>
/// instead of throwing and <c>SystemController.cs:356-360</c> then answers 400. The client retries the
/// write and the wizard step it could not record still renders, so the flow looks like it works.
///
/// These tests read the client union out of the source file and drive both directions across it, so a
/// step added on one side alone fails here rather than in a browser.
/// </summary>
public sealed class SetupStepWireContractTests : IDisposable
{
    // The client union has never carried "depot-mapping"; the server enum has. Depot mapping runs as
    // part of the Steam depot steps on the client rather than as a step of its own, so the two lists
    // disagree by exactly this one value. It is named here rather than fixed because closing the gap
    // means either adding a step the client never renders or dropping a value that may already sit in
    // a persisted state file. Adding it to the union is a fine change to make - it just has to come
    // off this list in the same commit.
    private static readonly SetupStep[] _stepsAbsentFromClientUnion = [SetupStep.DepotMapping];

    private readonly string _root;

    public SetupStepWireContractTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-setup-step-" + Guid.NewGuid().ToString("N"));
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
    public void EveryClientStepRoundTripsThroughTheServerEnum()
    {
        var clientSteps = ReadClientInitStepUnion();

        Assert.NotEmpty(clientSteps);

        foreach (var wire in clientSteps)
        {
            var parsed = SetupStepExtensions.TryParseWire(wire);

            Assert.True(
                parsed.HasValue && parsed.Value != SetupStep.Unknown,
                $"'{wire}' is in the client InitStep union but SetupStep.TryParseWire does not "
                + "recognise it, so PATCH /api/system/setup answers 400 for that step");
            Assert.Equal(wire, parsed!.Value.ToWireString());
        }
    }

    [Fact]
    public void EveryServerStepReachesTheClientUnionExceptTheOnesNamedHere()
    {
        var clientSteps = ReadClientInitStepUnion();

        var missing = Enum.GetValues<SetupStep>()
            .Where(step => step != SetupStep.Unknown)
            .Where(step => !clientSteps.Contains(step.ToWireString()))
            .ToArray();

        Assert.Equal(_stepsAbsentFromClientUnion, missing);
    }

    [Fact]
    public void TheAdminAccountStepIsAcceptedByTheSetupPatch()
    {
        var request = JsonSerializer.Deserialize<UpdateSetupStatusRequest>(
            """{"currentSetupStep":"admin-account"}""");

        Assert.NotNull(request);
        Assert.Equal(SetupStep.AdminAccount, request.CurrentSetupStep);

        var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)pathResolver).Root = _root;

        // Only the state service is reached by a body that carries nothing but a step: the Steam
        // service is touched only on a data-source choice and the rest on other actions.
        var controller = new SystemController(
            CreateStateService(_root),
            new ConfigurationBuilder().Build(),
            NullLogger<SystemController>.Instance,
            pathResolver,
            cacheClearingService: null!,
            steamKit2Service: null!,
            datasourceService: null!,
            notifications: null!,
            userPreferencesService: null!,
            capabilityService: null!,
            nginxLogRotationService: null!,
            cacheManagementService: null!,
            dbContextFactory: null!,
            claimWindow: null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = controller.UpdateSetupStatus(request);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    private static HashSet<string> ReadClientInitStepUnion()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Web", "src", "hooks", "useInitializationFlow.ts"));

        var declaration = Regex.Match(
            source,
            @"export type InitStep =(?<members>[^;]*);",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(
            declaration.Success,
            "the InitStep union was not found in useInitializationFlow.ts - if it moved, point this "
            + "test at its new home rather than deleting the check");

        return Regex
            .Matches(
                declaration.Groups["members"].Value,
                @"'(?<step>[^']+)'",
                RegexOptions.None,
                TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["step"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }
}

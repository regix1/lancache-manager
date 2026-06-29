using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the scheduled-prefill follow-ups: (1) a partial per-service failure must report the run
/// as unsuccessful in <c>ScheduledPrefillCompleted</c> (via the pure <see cref="ScheduledPrefillRunGates.EvaluateRunOutcome"/>
/// helper the orchestrator now delegates to), and (2) the DI-boot smoke test proving the
/// auth-orchestrator rip-out left the container able to activate <see cref="ScheduledPrefillService"/>
/// without the deleted scheduled-prefill auth-orchestrator dependency.
/// </summary>
public class ScheduledPrefillServiceTests
{
    // ---- Criterion 5: partial per-service failure must report success:false ----

    [Fact]
    public void EvaluateRunOutcome_ReportsSuccess_WhenServicesAttemptedAndNoneFailed()
    {
        var outcome = ScheduledPrefillRunGates.EvaluateRunOutcome(servicesAttempted: 3, anyServiceFailed: false);

        Assert.True(outcome.Success);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void EvaluateRunOutcome_ReportsFailure_WhenAServiceFailed_EvenIfOthersAttempted()
    {
        // A service threw (per-service catch) or returned false (skipped / failed to engage) during
        // an otherwise-progressing run — the run as a whole must not claim full success.
        var outcome = ScheduledPrefillRunGates.EvaluateRunOutcome(servicesAttempted: 2, anyServiceFailed: true);

        Assert.False(outcome.Success);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
    }

    [Fact]
    public void EvaluateRunOutcome_ReportsFailure_WhenNoServiceWasAttempted()
    {
        var outcome = ScheduledPrefillRunGates.EvaluateRunOutcome(servicesAttempted: 0, anyServiceFailed: false);

        Assert.False(outcome.Success);
        Assert.Equal("All enabled services were skipped", outcome.Error);
    }

    // ---- Criterion 3: DI-boot smoke test for the auth-orchestrator rip-out ----
    // After removing the dead auth-orchestrator dependency from the ScheduledPrefillService
    // constructor and from Program.cs DI, the container must still build and the hosted service must
    // activate WITHOUT that (now deleted) dependency. ValidateOnBuild proves the constructor's
    // call-site graph resolves with no missing dependency (a missing one throws here); the explicit
    // resolve then runs the real constructor. A plain unit test would not catch a DI-startup crash.

    [Fact]
    public void ServiceProvider_BuildsAndActivatesScheduledPrefillService_WithoutAuthService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IStateService>(CreateNullStateService());
        services.AddSingleton<ScheduledPrefillService>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var resolved = provider.GetRequiredService<ScheduledPrefillService>();

        Assert.NotNull(resolved);
    }

    private static IStateService CreateNullStateService()
        => (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();

    /// <summary>
    /// Minimal <see cref="IStateService"/> stub. The <see cref="ScheduledPrefillService"/> constructor
    /// only reads <c>GetServiceInterval</c> / <c>GetServiceRunOnStartup</c> (both nullable) via
    /// <c>LoadStateOverrides</c>; returning null is the "no saved override" path. Every other member
    /// returns its type default — none are exercised during construction.
    /// </summary>
    private class NullReturningProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                throw new InvalidOperationException("Target method was null.");
            }

            var returnType = targetMethod.ReturnType;

            if (returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            // Non-nullable value types need a concrete default; reference types and Nullable<T>
            // (e.g. double? / bool?) resolve to null.
            if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
            {
                return Activator.CreateInstance(returnType);
            }

            return null;
        }
    }
}

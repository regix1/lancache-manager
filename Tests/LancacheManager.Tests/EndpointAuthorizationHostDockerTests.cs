using System.Diagnostics;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LancacheManager.Tests;

/// <summary>
/// A booted test host runs the whole app on the developer's machine, so anything it hands the prefill
/// daemons or the nginx log service acts on the machine's real Docker: containers, login volumes and the
/// running nginx. This reads what the host was given once it is built and stops it before it starts, so
/// the check itself never reaches Docker.
/// </summary>
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class EndpointAuthorizationHostDockerTests
{
    [Fact]
    public void TheTestHostNeverHandsTheAppARealDockerPath()
    {
        var apiRoot = Path.Combine(EndpointAuthorizationHost.FindRepositoryRoot(), "Api", "LancacheManager");
        var observer = new BuiltHostObserver(apiRoot);
        var subscription = DiagnosticListener.AllListeners.Subscribe(observer);
        try
        {
            Assert.ThrowsAny<Exception>(() => new EndpointAuthorizationHost());
        }
        finally
        {
            subscription.Dispose();
            observer.Dispose();
        }

        // Compared by name: the nginx double is a test type, and the check has to build against code
        // that does not have it yet.
        Assert.Equal(nameof(UnavailableContainerGatewayFactory), observer.GatewayCreatorType?.Name);
        Assert.Equal("NoCommandNginxLogRotationService", observer.NginxServiceType?.Name);
    }

    private sealed class BuiltHostStopped : Exception;

    private sealed class BuiltHostObserver(string apiRoot)
        : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly List<IDisposable> _subscriptions = [];

        public Type? GatewayCreatorType { get; private set; }
        public Type? NginxServiceType { get; private set; }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.Extensions.Hosting")
            {
                lock (_subscriptions) _subscriptions.Add(listener.Subscribe(this));
            }
        }

        public void OnNext(KeyValuePair<string, object?> hostingEvent)
        {
            if (hostingEvent.Key != "HostBuilt" || hostingEvent.Value is not IHost host)
            {
                return;
            }

            // Hosts other test classes build in parallel have another content root and no daemons.
            var contentRoot = host.Services.GetRequiredService<IHostEnvironment>().ContentRootPath;
            if (!string.Equals(
                    contentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    apiRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)
                || host.Services.GetService<IPrefillContainerGatewayFactory>() is not { } gatewayFactory)
            {
                return;
            }

            GatewayCreatorType = gatewayFactory.GetType();
            NginxServiceType = host.Services.GetService<NginxLogRotationService>()?.GetType();
            throw new BuiltHostStopped();
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            lock (_subscriptions)
            {
                foreach (var subscription in _subscriptions)
                {
                    subscription.Dispose();
                }
            }
        }
    }
}

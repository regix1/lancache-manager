using System.Reflection;
using Docker.DotNet.Models;
using LancacheManager.Core.Services;

namespace LancacheManager.Tests;

/// <summary>
/// The addresses recorded for a prefill container are the ones the cache logs for it, and prefill
/// separation marks every download from them. An address another container also sends from, such
/// as the bridge gateway, must never be in the list.
/// </summary>
public sealed class PrefillContainerAddressTests
{
    private const string Own = "172.17.0.2";
    private const string Gateway = "172.17.0.1";

    [Fact]
    public void ABridgeContainerRecordsItsOwnAddressAndNotTheGateway()
    {
        var addresses = Read(Inspect(("bridge", Own, Gateway)));

        Assert.Equal(new[] { Own }, addresses);
    }

    [Fact]
    public void EveryAttachedNetworkRecordsItsOwnAddressOnly()
    {
        var addresses = Read(Inspect(("bridge", Own, Gateway), ("cache-net", "172.20.0.5", Gateway)));

        Assert.Equal(new[] { Own, "172.20.0.5" }, addresses);
    }

    [Fact]
    public void AHostNetworkContainerRecordsNothing()
    {
        var addresses = Read(Inspect(("host", "", "")));

        Assert.Empty(addresses);
    }

    [Fact]
    public void AContainerWithoutNetworkSettingsRecordsNothing()
    {
        var addresses = Read(new ContainerInspectResponse());

        Assert.Empty(addresses);
    }

    private static ContainerInspectResponse Inspect(params (string Name, string Address, string Gateway)[] networks)
        => new()
        {
            NetworkSettings = new NetworkSettings
            {
                Networks = networks.ToDictionary(
                    network => network.Name,
                    network => new EndpointSettings { IPAddress = network.Address, Gateway = network.Gateway })
            }
        };

    private static List<string> Read(ContainerInspectResponse inspect)
    {
        var method = typeof(PrefillDaemonServiceBase).GetMethod(
            "ReadPrefillAddresses",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"ReadPrefillAddresses not found on {nameof(PrefillDaemonServiceBase)}");
        return (List<string>)method.Invoke(null, new object[] { inspect })!;
    }
}

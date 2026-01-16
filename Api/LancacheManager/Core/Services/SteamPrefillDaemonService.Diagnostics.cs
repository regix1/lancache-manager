using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet.Models;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

public partial class SteamPrefillDaemonService
{
    private async Task<NetworkDiagnostics> TestContainerConnectivityAsync(string containerId, string containerName, bool isHostMode, CancellationToken cancellationToken = default)
    {
        var diagnostics = new NetworkDiagnostics
        {
            UseHostNetworking = isHostMode
        };

        if (_dockerClient == null) return diagnostics;

        _logger.LogInformation("═══════════════════════════════════════════════════════════════════════");
        _logger.LogInformation("  PREFILL CONTAINER NETWORK DIAGNOSTICS - {ContainerName}", containerName);
        _logger.LogInformation("═══════════════════════════════════════════════════════════════════════");

        // Test 1: Internet connectivity (try to reach Steam API)
        var (internetSuccess, internetError) = await TestInternetConnectivityInContainerAsync(containerId, cancellationToken);
        diagnostics.InternetConnectivity = internetSuccess;
        diagnostics.InternetConnectivityError = internetError;
        var (ipv4Success, ipv4Error) = await TestInternetConnectivityByIpFamilyAsync(containerId, AddressFamily.InterNetwork, cancellationToken);
        diagnostics.InternetConnectivityIpv4 = ipv4Success;
        diagnostics.InternetConnectivityIpv4Error = ipv4Error;
        var (ipv6Success, ipv6Error) = await TestInternetConnectivityByIpFamilyAsync(containerId, AddressFamily.InterNetworkV6, cancellationToken);
        diagnostics.InternetConnectivityIpv6 = ipv6Success;
        diagnostics.InternetConnectivityIpv6Error = ipv6Error;

        // Test 2: DNS resolution for lancache domains
        var dnsResult1 = await TestDnsResolutionInContainerAsync(containerId, "lancache.steamcontent.com", cancellationToken);
        var dnsResult2 = await TestDnsResolutionInContainerAsync(containerId, "steam.cache.lancache.net", cancellationToken);
        diagnostics.DnsResults.Add(dnsResult1);
        diagnostics.DnsResults.Add(dnsResult2);

        _logger.LogInformation("═══════════════════════════════════════════════════════════════════════");
        _logger.LogInformation("  END NETWORK DIAGNOSTICS");
        _logger.LogInformation("═══════════════════════════════════════════════════════════════════════");

        return diagnostics;
    }

    /// <summary>
    /// Tests internet connectivity from inside a container by attempting to reach Steam API.
    /// </summary>
    private async Task<(bool Success, string? Error)> TestInternetConnectivityInContainerAsync(string containerId, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("───────────────────────────────────────────────────────────────────────");
            _logger.LogInformation("  Testing Internet Connectivity...");
            _logger.LogInformation("───────────────────────────────────────────────────────────────────────");

            // Use wget with timeout to test connectivity (most minimal images have wget or curl)
            // Try wget first (Alpine-based images), then curl as fallback
            var testCommands = new[]
            {
                new[] { "wget", "-q", "-O", "-", "--timeout=10", "https://api.steampowered.com/" },
                new[] { "curl", "-s", "-m", "10", "https://api.steampowered.com/" }
            };

            string? lastError = null;

            foreach (var cmd in testCommands)
            {
                try
                {
                    var (exitCode, _) = await ExecuteContainerCommandAsync(containerId, cmd, cancellationToken);
                    if (exitCode == 0)
                    {
                        _logger.LogInformation("  ✓ Internet connectivity: OK (reached api.steampowered.com)");
                        return (true, null);
                    }
                    lastError = $"Command {cmd[0]} failed with exit code {exitCode}";
                }
                catch (Exception ex)
                {
                    lastError = $"{cmd[0]}: {ex.Message}";
                }
            }

            _logger.LogWarning("  ✗ Internet connectivity: FAILED");
            _logger.LogWarning("    The prefill container cannot reach the internet.");
            _logger.LogWarning("    Steam login and prefill will not work.");
            _logger.LogWarning("    Error: {Error}", lastError);
            _logger.LogWarning("    ");
            _logger.LogWarning("    Possible fixes:");
            _logger.LogWarning("    - Try setting Prefill__NetworkMode=bridge in your docker-compose.yml");
            _logger.LogWarning("    - Ensure your Docker network has internet access");
            _logger.LogWarning("    - Check firewall rules for outbound connections");

            return (false, lastError ?? "No connectivity tool available");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "  Could not test internet connectivity in container");
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Tests internet connectivity for a specific IP family (IPv4 or IPv6) from inside a container.
    /// Returns null when the container lacks tools that support the requested family flag.
    /// </summary>
    private async Task<(bool? Success, string? Error)> TestInternetConnectivityByIpFamilyAsync(
        string containerId,
        AddressFamily family,
        CancellationToken cancellationToken)
    {
        var flag = family == AddressFamily.InterNetwork ? "-4" : "-6";
        var familyName = family == AddressFamily.InterNetwork ? "IPv4" : "IPv6";

        var testCommands = new[]
        {
            new[] { "wget", "-q", "-O", "-", "--timeout=10", flag, "https://api.steampowered.com/" },
            new[] { "curl", "-s", "-m", "10", flag, "https://api.steampowered.com/" }
        };

        string? lastError = null;
        var supportedCommandAttempted = false;

        foreach (var cmd in testCommands)
        {
            try
            {
                var (exitCode, output) = await ExecuteContainerCommandAsync(containerId, cmd, cancellationToken);
                if (exitCode == 0)
                {
                    _logger.LogInformation("  ✓ {Family} connectivity: OK (reached api.steampowered.com)", familyName);
                    return (true, null);
                }

                if (IsUnsupportedToolOutput(output))
                {
                    lastError = $"{cmd[0]} does not support {flag}";
                    continue;
                }

                supportedCommandAttempted = true;
                lastError = $"Command {cmd[0]} {flag} failed with exit code {exitCode}";
            }
            catch (Exception ex)
            {
                if (IsUnsupportedToolOutput(ex.Message))
                {
                    lastError = $"{cmd[0]} does not support {flag}";
                    continue;
                }

                supportedCommandAttempted = true;
                lastError = $"{cmd[0]}: {ex.Message}";
            }
        }

        if (!supportedCommandAttempted)
        {
            _logger.LogInformation("  • {Family} connectivity: Not tested (tooling not available)", familyName);
            return (null, lastError);
        }

        _logger.LogWarning("  ✗ {Family} connectivity: FAILED ({Error})", familyName, lastError);
        return (false, lastError);
    }

    /// <summary>
    /// Tests DNS resolution for a specific domain from inside a container.
    /// For lancache domains, this should resolve to your cache server IP.
    /// </summary>
    private async Task<DnsTestResult> TestDnsResolutionInContainerAsync(string containerId, string domain, CancellationToken cancellationToken)
    {
        var result = new DnsTestResult { Domain = domain };
        
        try
        {
            _logger.LogInformation("───────────────────────────────────────────────────────────────────────");
            _logger.LogInformation("  Testing DNS Resolution for {Domain}...", domain);
            _logger.LogInformation("───────────────────────────────────────────────────────────────────────");

            // Try multiple methods to resolve DNS (nslookup, getent, or ping)
            var dnsCommands = new[]
            {
                new[] { "nslookup", domain },
                new[] { "getent", "hosts", domain },
                new[] { "ping", "-c", "1", "-W", "2", domain }
            };

            List<string>? resolvedIps = null;
            string? lastError = null;

            foreach (var cmd in dnsCommands)
            {
                try
                {
                    var (exitCode, output) = await ExecuteContainerCommandAsync(containerId, cmd, cancellationToken);
                    if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        // Extract IPs from output
                        resolvedIps = ExtractIpsFromOutput(output, cmd[0]);
                        if (resolvedIps.Count > 0)
                        {
                            break;
                        }
                    }
                    lastError = $"Command {cmd[0]} returned no IP";
                }
                catch (Exception ex)
                {
                    lastError = $"{cmd[0]}: {ex.Message}";
                }
            }

            if (resolvedIps != null && resolvedIps.Count > 0)
            {
                result.Success = true;
                result.ResolvedIps = resolvedIps;
                result.IsPrivateIp = resolvedIps.Any(IsPrivateIp);
                
                _logger.LogInformation("  {Domain} resolved to {IpAddresses}", domain, string.Join(", ", resolvedIps));
                
                // Check if it's a lancache IP (typically private IPs like 192.168.x.x, 10.x.x.x, etc.)
                if (result.IsPrivateIp)
                {
                    _logger.LogInformation("  ✓ DNS looks correct (private IP - likely your lancache server)");
                }
                else
                {
                    _logger.LogInformation("  ⚠ DNS resolved to a public IP ({IpAddress})", string.Join(", ", resolvedIps));
                }
            }
            else
            {
                result.Success = false;
                result.Error = lastError ?? "Could not resolve domain";
                
                _logger.LogWarning("  ✗ Could not resolve {Domain}", domain);
                _logger.LogWarning("    Error: {Error}", lastError);
                _logger.LogWarning("    ");
                _logger.LogWarning("    If this is expected (no lancache-dns), you can ignore this warning.");
                _logger.LogWarning("    Otherwise, check your DNS configuration.");
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger.LogWarning(ex, "  Could not test DNS resolution for {Domain}", domain);
        }

        return result;
    }

    /// <summary>
    /// Executes a command inside a container and returns the exit code and output.
    /// </summary>
    private async Task<(long exitCode, string output)> ExecuteContainerCommandAsync(
        string containerId, 
        string[] command, 
        CancellationToken cancellationToken)
    {
        if (_dockerClient == null)
        {
            throw new InvalidOperationException("Docker client not available");
        }

        // Create exec instance
        var execCreateResponse = await _dockerClient.Exec.ExecCreateContainerAsync(
            containerId,
            new ContainerExecCreateParameters
            {
                Cmd = command,
                AttachStdout = true,
                AttachStderr = true
            },
            cancellationToken);

        // Start exec and capture output
        using var stream = await _dockerClient.Exec.StartAndAttachContainerExecAsync(
            execCreateResponse.ID,
            false,
            cancellationToken);

        // Read output (with timeout)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var memoryStream = new MemoryStream();
        await stream.CopyOutputToAsync(null, memoryStream, null, cts.Token);
        memoryStream.Position = 0;
        using var reader = new StreamReader(memoryStream);
        var output = await reader.ReadToEndAsync(cts.Token);

        // Get exit code
        var execInspect = await _dockerClient.Exec.InspectContainerExecAsync(execCreateResponse.ID, cancellationToken);
        
        return (execInspect.ExitCode, output);
    }

    /// <summary>
    /// Extracts an IP address from command output based on the command type.
    /// </summary>
    private static List<string> ExtractIpsFromOutput(string output, string command)
    {
        var ipv4Pattern = @"\b(?:\d{1,3}\.){3}\d{1,3}\b";
        var ipv6Pattern = @"\b(?:[0-9a-fA-F]{0,4}:){2,7}[0-9a-fA-F]{0,4}\b";
        var ips = new List<string>();
        
        if (command == "nslookup")
        {
            // nslookup output format:
            // Server:  dns-server
            // Address:  8.8.8.8       <-- DNS server, skip this
            //
            // Name:    domain.com
            // Address: 172.16.2.98   <-- Resolved IP, we want this
            //
            // Look for IP after "Name:" line
            var lines = output.Split('\n');
            bool foundNameLine = false;
            
            foreach (var line in lines)
            {
                if (line.Contains("Name:"))
                {
                    foundNameLine = true;
                    continue;
                }
                
                if (foundNameLine && line.Contains("Address"))
                {
                    ips.AddRange(ExtractIpsFromLine(line, ipv4Pattern, ipv6Pattern));
                }
            }
            
            return FilterIps(ips);
        }
        
        // For getent and ping, just find the first non-loopback IP
        ips.AddRange(ExtractIpsFromText(output, ipv4Pattern, ipv6Pattern));
        return FilterIps(ips);
    }

    /// <summary>
    /// Checks if an IP address is in a private range (RFC 1918 or IPv6 ULA/link-local).
    /// </summary>
    private static bool IsPrivateIp(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return false;
        if (!IPAddress.TryParse(ip, out var address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            var first = bytes[0];
            var second = bytes[1];

            // 10.0.0.0/8
            if (first == 10) return true;

            // 172.16.0.0/12
            if (first == 172 && second >= 16 && second <= 31) return true;

            // 192.168.0.0/16
            if (first == 192 && second == 168) return true;

            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;

            var bytes = address.GetAddressBytes();
            // Unique local addresses (fc00::/7)
            return (bytes[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    private static List<string> ExtractIpsFromLine(string line, string ipv4Pattern, string ipv6Pattern)
    {
        var ips = new List<string>();
        ips.AddRange(ExtractMatches(line, ipv4Pattern));
        ips.AddRange(ExtractMatches(line, ipv6Pattern));
        return ips;
    }

    private static List<string> ExtractIpsFromText(string text, string ipv4Pattern, string ipv6Pattern)
    {
        var ips = new List<string>();
        ips.AddRange(ExtractMatches(text, ipv4Pattern));
        ips.AddRange(ExtractMatches(text, ipv6Pattern));
        return ips;
    }

    private static List<string> ExtractMatches(string text, string pattern)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(text, pattern);
        var results = new List<string>();
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            results.Add(match.Value);
        }

        return results;
    }

    private static List<string> FilterIps(List<string> ips)
    {
        var filtered = new List<string>();
        foreach (var ip in ips)
        {
            if (!IPAddress.TryParse(ip, out var address)) continue;
            if (IPAddress.IsLoopback(address)) continue;
            if (filtered.Contains(ip)) continue;
            filtered.Add(ip);
        }

        return filtered;
    }

    private static bool IsIpv4(string ip)
    {
        return IPAddress.TryParse(ip, out var address) && address.AddressFamily == AddressFamily.InterNetwork;
    }

    private static bool IsIpv6(string ip)
    {
        return IPAddress.TryParse(ip, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;
    }

    private static bool IsUnsupportedToolOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        var lower = output.ToLowerInvariant();
        return lower.Contains("not found") ||
               lower.Contains("unknown option") ||
               lower.Contains("unrecognized option") ||
               lower.Contains("invalid option");
    }

    }

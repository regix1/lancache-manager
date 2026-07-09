using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.StatusCheck;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Admin-only DNS diagnostics: server-side sweep of every cache-domains entry against the lancache
/// server's expected IP(s), an ad hoc single-domain test, and the cache-domains list backing the
/// test-a-domain dropdown.
/// </summary>
[ApiController]
[Route("api/status-check")]
[Authorize(Policy = "AdminOnly")]
public class StatusCheckController : ControllerBase
{
    private readonly IStatusCheckService _statusCheckService;
    private readonly ICacheDomainsService _domainsService;

    public StatusCheckController(IStatusCheckService statusCheckService, ICacheDomainsService domainsService)
    {
        _statusCheckService = statusCheckService;
        _domainsService = domainsService;
    }

    [HttpGet("")]
    public ActionResult<StatusCheckStateResponse> GetState()
    {
        return Ok(new StatusCheckStateResponse
        {
            LastResult = _statusCheckService.GetLastResult(),
            DomainsSource = _domainsService.GetCurrentSource(),
            IsRunning = _statusCheckService.IsRunning,
            OperationId = _statusCheckService.CurrentOperationId,
            ResolverMode = _statusCheckService.GetResolverMode()
        });
    }

    [HttpPost("resolver-mode")]
    public ActionResult<SetResolverModeResponse> SetResolverMode([FromBody] SetResolverModeRequest request)
    {
        if (!StatusCheckResolverModes.IsValid(request.Mode))
        {
            return BadRequest(ApiResponse.Invalid("mode must be one of: auto, bridge, host."));
        }

        _statusCheckService.SetResolverMode(request.Mode);
        return Ok(new SetResolverModeResponse { ResolverMode = request.Mode });
    }

    [HttpPost("run")]
    public ActionResult Run()
    {
        var operationId = _statusCheckService.StartSweep();
        if (operationId == null)
        {
            throw new ConflictException("A Status Check sweep is already running.");
        }

        return Accepted(new RunStatusCheckResponse { OperationId = operationId.Value });
    }

    [HttpPost("test-domain")]
    public async Task<ActionResult<TestDomainResponse>> TestDomainAsync(
        [FromBody] TestDomainRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Domain))
        {
            return BadRequest(ApiResponse.Invalid("domain is required."));
        }

        // Wildcard cache-domains entries (*.cdn.example.com) are legal test input - the sweep's
        // probe-label substitution happens service-side; validate the part after the wildcard.
        var domain = request.Domain.Trim();
        var hostnameToValidate = domain.StartsWith("*.", StringComparison.Ordinal) ? domain[2..] : domain;
        if (domain.Length > 253 || Uri.CheckHostName(hostnameToValidate) == UriHostNameType.Unknown)
        {
            return BadRequest(ApiResponse.Invalid("domain is not a valid hostname."));
        }

        var (result, heartbeat) = await _statusCheckService.TestDomainAsync(domain, cancellationToken);
        return Ok(new TestDomainResponse { Result = result, Heartbeat = heartbeat });
    }

    [HttpPost("refresh-domains")]
    public async Task<ActionResult> RefreshDomainsAsync(CancellationToken cancellationToken)
    {
        var outcome = await _domainsService.RefreshDomainsAsync(cancellationToken);
        if (!outcome.Success)
        {
            throw new ConflictException(outcome.BlockedReason ?? "Domain refresh was blocked.");
        }

        return Ok(new RefreshDomainsResponse
        {
            DomainsSource = outcome.Source,
            ServiceCount = outcome.Domains.Services.Count,
            DomainCount = outcome.Domains.Services.Sum(s => s.Domains.Count)
        });
    }

    [HttpGet("domains")]
    public async Task<ActionResult<GetDomainsResponse>> GetDomainsAsync(CancellationToken cancellationToken)
    {
        var domains = await _domainsService.GetDomainsAsync(forceRefresh: false, cancellationToken);
        return Ok(new GetDomainsResponse { Services = domains.Services });
    }
}

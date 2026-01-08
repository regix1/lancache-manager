import { useState } from 'react';
import { CheckCircle2, XCircle, AlertTriangle, Wifi, Globe, Server, Info, ChevronDown } from 'lucide-react';
import { Card } from '../../ui/Card';
import type { NetworkDiagnostics } from '@services/api.service';

interface NetworkStatusSectionProps {
  diagnostics: NetworkDiagnostics | undefined;
}

export function NetworkStatusSection({ diagnostics }: NetworkStatusSectionProps) {
  const [isExpanded, setIsExpanded] = useState(false);

  if (!diagnostics) {
    return null;
  }

  // With host networking, public DNS IPs are expected - steam-prefill detects lancache via localhost/gateway
  const hasPublicDnsWithHostNetworking = diagnostics.useHostNetworking &&
    diagnostics.dnsResults.some(r => r.success && !r.isPrivateIp);

  // Only flag as issue if: no internet, DNS failed, or public IP without host networking
  const hasRealIssue = !diagnostics.internetConnectivity ||
    diagnostics.dnsResults.some(r => !r.success) ||
    (!diagnostics.useHostNetworking && diagnostics.dnsResults.some(r => r.success && !r.isPrivateIp));

  // Show info state (not warning) when public DNS but host networking is in use
  const showInfoState = hasPublicDnsWithHostNetworking && !hasRealIssue;

  const statusColor = hasRealIssue
    ? 'var(--theme-warning)'
    : showInfoState
      ? 'var(--theme-info)'
      : 'var(--theme-success)';

  const statusBgColor = hasRealIssue
    ? 'color-mix(in srgb, var(--theme-warning) 15%, transparent)'
    : showInfoState
      ? 'color-mix(in srgb, var(--theme-info) 15%, transparent)'
      : 'color-mix(in srgb, var(--theme-success) 15%, transparent)';

  return (
    <Card padding="md">
      <div className="space-y-3">
        {/* Clickable Header */}
        <button
          onClick={() => setIsExpanded(!isExpanded)}
          className="flex items-center gap-3 w-full text-left hover:opacity-80 transition-opacity"
        >
          <div
            className="w-10 h-10 rounded-lg flex items-center justify-center flex-shrink-0"
            style={{ backgroundColor: statusBgColor }}
          >
            <Wifi className="h-5 w-5" style={{ color: statusColor }} />
          </div>
          <div className="flex-1 min-w-0">
            <p className="font-medium text-themed-primary">Container Network Status</p>
            <p className="text-sm text-themed-muted">
              {hasRealIssue
                ? 'Some issues detected'
                : showInfoState
                  ? 'Host networking mode'
                  : 'All checks passed'}
            </p>
          </div>
          <ChevronDown
            className={`h-5 w-5 text-themed-muted transition-transform duration-200 flex-shrink-0 ${
              isExpanded ? 'rotate-180' : ''
            }`}
          />
        </button>

        {/* Collapsible Status Items */}
        {isExpanded && (
          <div className="space-y-2 pl-1 pt-2 border-t border-themed-secondary">
          {/* Internet Connectivity */}
          <div className="flex items-center gap-2">
            {diagnostics.internetConnectivity ? (
              <CheckCircle2 className="h-4 w-4 flex-shrink-0 text-[var(--theme-success)]" />
            ) : (
              <XCircle className="h-4 w-4 flex-shrink-0 text-[var(--theme-error)]" />
            )}
            <Globe className="h-4 w-4 text-themed-muted flex-shrink-0" />
            <span className="text-sm text-themed-primary">Internet connectivity</span>
            {diagnostics.internetConnectivity ? (
              <span className="text-xs text-themed-muted ml-auto">OK</span>
            ) : (
              <span className="text-xs ml-auto text-[var(--theme-error)]">
                Failed
              </span>
            )}
          </div>

          {/* Internet Error Details */}
          {!diagnostics.internetConnectivity && diagnostics.internetConnectivityError && (
            <div className="ml-6 text-xs p-2 rounded bg-[var(--theme-error-bg)] text-[var(--theme-error-text)]">
              {diagnostics.internetConnectivityError}
              <div className="mt-1 text-themed-muted">
                Try setting <code className="px-1 py-0.5 rounded bg-themed-tertiary">Prefill__NetworkMode=bridge</code> in your docker-compose.yml
              </div>
            </div>
          )}

          {/* DNS Results */}
          {diagnostics.dnsResults.map((result, index) => (
            <div key={index} className="space-y-1">
              <div className="flex items-center gap-2">
                {result.success ? (
                  result.isPrivateIp ? (
                    <CheckCircle2 className="h-4 w-4 flex-shrink-0 text-[var(--theme-success)]" />
                  ) : diagnostics.useHostNetworking ? (
                    <Info className="h-4 w-4 flex-shrink-0 text-[var(--theme-info)]" />
                  ) : (
                    <AlertTriangle className="h-4 w-4 flex-shrink-0 text-[var(--theme-warning)]" />
                  )
                ) : (
                  <XCircle className="h-4 w-4 flex-shrink-0 text-[var(--theme-error)]" />
                )}
                <Server className="h-4 w-4 text-themed-muted flex-shrink-0" />
                <span className="text-sm text-themed-primary truncate" title={result.domain}>
                  {result.domain}
                </span>
                {result.success ? (
                  <span
                    className="text-xs ml-auto font-mono"
                    style={{
                      color: result.isPrivateIp
                        ? 'var(--theme-success)'
                        : diagnostics.useHostNetworking
                          ? 'var(--theme-info)'
                          : 'var(--theme-warning)'
                    }}
                  >
                    {result.resolvedIp}
                  </span>
                ) : (
                  <span className="text-xs ml-auto text-[var(--theme-error)]">
                    Not resolved
                  </span>
                )}
              </div>

              {/* DNS Info/Warning for public IP - different message based on host networking */}
              {result.success && !result.isPrivateIp && (
                <div
                  className="ml-6 text-xs p-2 rounded"
                  style={{
                    backgroundColor: diagnostics.useHostNetworking
                      ? 'var(--theme-info-bg)'
                      : 'var(--theme-warning-bg)',
                    color: diagnostics.useHostNetworking
                      ? 'var(--theme-info-text)'
                      : 'var(--theme-warning-text)'
                  }}
                >
                  {diagnostics.useHostNetworking ? (
                    <>
                      Public DNS is expected with host networking. Steam-prefill will detect
                      lancache automatically via localhost. If your cache size grows during
                      downloads, everything is working correctly.
                    </>
                  ) : (
                    <>
                      Public IP detected - lancache-dns may not be configured.
                      Prefill may download from internet instead of populating cache.
                    </>
                  )}
                </div>
              )}

              {/* DNS Error Details */}
              {!result.success && result.error && (
                <div className="ml-6 text-xs p-2 rounded bg-[var(--theme-error-bg)] text-[var(--theme-error-text)]">
                  {result.error}
                </div>
              )}
            </div>
          ))}
          </div>
        )}
      </div>
    </Card>
  );
}

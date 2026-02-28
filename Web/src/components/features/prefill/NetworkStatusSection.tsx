import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  CheckCircle2,
  XCircle,
  AlertTriangle,
  Wifi,
  Globe,
  Server,
  Info,
  ChevronDown
} from 'lucide-react';
import { Card } from '../../ui/Card';
import type { NetworkDiagnostics } from '@services/api.service';

interface NetworkStatusSectionProps {
  diagnostics: NetworkDiagnostics | undefined;
}

function hasIpv6Resolution(result: NetworkDiagnostics['dnsResults'][number]) {
  if (!result.resolvedIps || result.resolvedIps.length === 0) {
    return false;
  }

  return result.resolvedIps.some((ip) => ip.includes(':'));
}

export function NetworkStatusSection({ diagnostics }: NetworkStatusSectionProps) {
  const { t } = useTranslation();
  const [isExpanded, setIsExpanded] = useState(false);

  if (!diagnostics) {
    return null;
  }

  // With host networking, public DNS IPs are expected - prefill daemon detects lancache via localhost/gateway
  const hasPublicDnsWithHostNetworking =
    diagnostics.useHostNetworking &&
    diagnostics.dnsResults.some((r) => r.success && !r.isPrivateIp);

  // The first DNS domain in the list is the primary/trigger domain for the service
  const primaryDomain = diagnostics.dnsResults.length > 0 ? diagnostics.dnsResults[0].domain : '';
  const primaryResult = diagnostics.dnsResults.length > 0 ? diagnostics.dnsResults[0] : undefined;
  const hasPrimaryConfigured = primaryResult?.success && primaryResult?.isPrivateIp;

  // IPv6 bypass is only an issue if the primary domain doesn't resolve to a private IP via IPv4
  const hasIpv6BypassIssue =
    !diagnostics.useHostNetworking &&
    !hasPrimaryConfigured &&
    diagnostics.dnsResults.some((r) => r.success && hasIpv6Resolution(r) && !r.isPrivateIp);

  // Only flag as issue if: no internet, or primary domain not configured (without host networking)
  const hasRealIssue =
    !diagnostics.internetConnectivity ||
    (!diagnostics.useHostNetworking && !hasPrimaryConfigured) ||
    hasIpv6BypassIssue;

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
            className="w-10 h-10 rounded-lg flex items-center justify-center flex-shrink-0 prefill-network-status-bg"
            style={{ '--network-status-bg': statusBgColor } as React.CSSProperties}
          >
            <Wifi
              className="h-5 w-5 prefill-network-status-icon"
              style={{ '--network-status-color': statusColor } as React.CSSProperties}
            />
          </div>
          <div className="flex-1 min-w-0">
            <p className="font-medium text-themed-primary">{t('prefill.network.title')}</p>
            <p className="text-sm text-themed-muted">
              {hasRealIssue
                ? t('prefill.network.issuesDetected')
                : showInfoState
                  ? t('prefill.network.hostNetworking')
                  : t('prefill.network.allChecksPassed')}
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
            {/* Host networking hint */}
            {diagnostics.useHostNetworking && (
              <div className="text-xs p-2.5 rounded bg-[var(--theme-info-bg)] text-[var(--theme-info-text)] leading-relaxed">
                {t('prefill.network.hostNetworkingHint')}
              </div>
            )}
            {/* Internet Connectivity */}
            <div className="flex items-center justify-between gap-2 flex-wrap">
              <div className="flex items-center gap-2">
                {diagnostics.internetConnectivity ? (
                  <CheckCircle2 className="h-4 w-4 flex-shrink-0 text-[var(--theme-success)]" />
                ) : (
                  <XCircle className="h-4 w-4 flex-shrink-0 text-[var(--theme-error)]" />
                )}
                <Globe className="h-4 w-4 text-themed-muted flex-shrink-0" />
                <span className="text-sm text-themed-primary">
                  {t('prefill.network.internetConnectivity')}
                </span>
              </div>
              {diagnostics.internetConnectivity ? (
                <span className="text-xs text-themed-muted">{t('prefill.network.ok')}</span>
              ) : (
                <span className="text-xs text-[var(--theme-error)]">
                  {t('prefill.network.failed')}
                </span>
              )}
            </div>

            {/* IPv4 Connectivity */}
            {diagnostics.internetConnectivityIpv4 !== undefined && (
              <div className="flex items-center justify-between gap-2 flex-wrap">
                <div className="flex items-center gap-2">
                  {diagnostics.internetConnectivityIpv4 === true ? (
                    <CheckCircle2 className="h-4 w-4 flex-shrink-0 text-[var(--theme-success)]" />
                  ) : diagnostics.internetConnectivityIpv4 === false ? (
                    <XCircle className="h-4 w-4 flex-shrink-0 text-[var(--theme-error)]" />
                  ) : (
                    <Info className="h-4 w-4 flex-shrink-0 text-[var(--theme-info)]" />
                  )}
                  <Globe className="h-4 w-4 text-themed-muted flex-shrink-0" />
                  <span className="text-sm text-themed-primary">
                    {t('prefill.network.ipv4Connectivity')}
                  </span>
                </div>
                {diagnostics.internetConnectivityIpv4 === true ? (
                  <span className="text-xs text-themed-muted">{t('prefill.network.ok')}</span>
                ) : diagnostics.internetConnectivityIpv4 === false ? (
                  <span className="text-xs text-[var(--theme-error)]">
                    {t('prefill.network.failed')}
                  </span>
                ) : (
                  <span className="text-xs text-[var(--theme-info)]">
                    {t('prefill.network.notTested')}
                  </span>
                )}
              </div>
            )}

            {/* IPv6 Connectivity */}
            {diagnostics.internetConnectivityIpv6 !== undefined && (
              <div className="flex items-center justify-between gap-2 flex-wrap">
                <div className="flex items-center gap-2">
                  {diagnostics.internetConnectivityIpv6 === false ? (
                    <CheckCircle2 className="h-4 w-4 flex-shrink-0 text-[var(--theme-success)]" />
                  ) : diagnostics.internetConnectivityIpv6 === true ? (
                    <XCircle className="h-4 w-4 flex-shrink-0 text-[var(--theme-error)]" />
                  ) : (
                    <Info className="h-4 w-4 flex-shrink-0 text-[var(--theme-info)]" />
                  )}
                  <Globe className="h-4 w-4 text-themed-muted flex-shrink-0" />
                  <span className="text-sm text-themed-primary">
                    {t('prefill.network.ipv6Connectivity')}
                  </span>
                </div>
                {diagnostics.internetConnectivityIpv6 === false ? (
                  <span className="text-xs text-[var(--theme-success)]">
                    {t('prefill.network.ipv6NotDetected')}
                  </span>
                ) : diagnostics.internetConnectivityIpv6 === true ? (
                  <span className="text-xs text-[var(--theme-error)]">
                    {t('prefill.network.ipv6Detected')}
                  </span>
                ) : (
                  <span className="text-xs text-[var(--theme-info)]">
                    {t('prefill.network.notTested')}
                  </span>
                )}
              </div>
            )}

            {diagnostics.internetConnectivityIpv6 === true && (
              <div className="ml-6 text-xs p-2.5 rounded leading-relaxed bg-[var(--theme-info-bg)] text-[var(--theme-info-text)]">
                {t('prefill.network.ipv6UnsupportedHint')}
              </div>
            )}

            {/* Internet Error Details */}
            {!diagnostics.internetConnectivity && diagnostics.internetConnectivityError && (
              <div className="ml-6 text-xs p-2.5 rounded leading-relaxed bg-[var(--theme-error-bg)] text-[var(--theme-error-text)]">
                {diagnostics.internetConnectivityError}
                <div className="mt-1.5 text-themed-muted">
                  {t('prefill.network.trySetting')}{' '}
                  <code className="px-1 py-0.5 rounded bg-themed-tertiary break-all">
                    Prefill__NetworkMode=bridge
                  </code>{' '}
                  {t('prefill.network.inDockerCompose')}
                </div>
              </div>
            )}

            {/* DNS Results */}
            {diagnostics.dnsResults.map((result, index) => {
              const isPrimaryDomain = result.domain === primaryDomain;
              // For secondary domains, show info style if primary domain is configured
              // For the primary domain, only show info with host networking
              const showAsInfo =
                diagnostics.useHostNetworking || (!isPrimaryDomain && hasPrimaryConfigured);

              return (
                <div key={index} className="space-y-1">
                  {/* Domain row */}
                  <div className="flex items-center justify-between gap-2 flex-wrap">
                    <div className="flex items-center gap-2 min-w-0">
                      {result.success ? (
                        result.isPrivateIp ? (
                          <CheckCircle2 className="h-4 w-4 flex-shrink-0 text-[var(--theme-success)]" />
                        ) : showAsInfo ? (
                          <Info className="h-4 w-4 flex-shrink-0 text-[var(--theme-info)]" />
                        ) : (
                          <AlertTriangle className="h-4 w-4 flex-shrink-0 text-[var(--theme-warning)]" />
                        )
                      ) : (
                        <XCircle className="h-4 w-4 flex-shrink-0 text-[var(--theme-error)]" />
                      )}
                      <Server className="h-4 w-4 text-themed-muted flex-shrink-0" />
                      <span className="text-sm text-themed-primary break-all" title={result.domain}>
                        {result.domain}
                      </span>
                    </div>
                    {!result.success && (
                      <span className="text-xs text-[var(--theme-error)]">
                        {t('prefill.network.notResolved')}
                      </span>
                    )}
                  </div>
                  {/* IP addresses on separate line for better mobile display */}
                  {result.success && result.resolvedIps && result.resolvedIps.length > 0 && (
                    <div
                      className={`ml-6 text-xs font-mono break-all ${
                        result.isPrivateIp
                          ? 'text-[var(--theme-success)]'
                          : showAsInfo
                            ? 'text-[var(--theme-info)]'
                            : 'text-[var(--theme-warning)]'
                      }`}
                    >
                      {result.resolvedIps.join(', ')}
                    </div>
                  )}

                  {/* DNS Info/Warning for public IP - different message based on domain and context */}
                  {result.success && !result.isPrivateIp && (
                    <div
                      className={`ml-6 text-xs p-2.5 rounded leading-relaxed ${
                        showAsInfo
                          ? 'bg-[var(--theme-info-bg)] text-[var(--theme-info-text)]'
                          : 'bg-[var(--theme-warning-bg)] text-[var(--theme-warning-text)]'
                      }`}
                    >
                      {diagnostics.useHostNetworking
                        ? t('prefill.network.publicDnsExpected')
                        : isPrimaryDomain
                          ? t('prefill.network.primaryDomainNotConfigured')
                          : hasPrimaryConfigured
                            ? t('prefill.network.optionalDomainNotConfigured')
                            : t('prefill.network.publicIpDetected')}
                    </div>
                  )}

                  {/* IPv6 bypass warning - only show if primary domain not configured */}
                  {result.success &&
                    hasIpv6Resolution(result) &&
                    !result.isPrivateIp &&
                    !diagnostics.useHostNetworking &&
                    !hasPrimaryConfigured && (
                      <div className="ml-6 text-xs p-2.5 rounded leading-relaxed bg-[var(--theme-warning-bg)] text-[var(--theme-warning-text)]">
                        {t('prefill.network.ipv6BypassDetected')}
                      </div>
                    )}

                  {/* DNS Error Details */}
                  {!result.success && result.error && (
                    <div className="ml-6 text-xs p-2.5 rounded leading-relaxed bg-[var(--theme-error-bg)] text-[var(--theme-error-text)]">
                      {result.error}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        )}
      </div>
    </Card>
  );
}

import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle2, XCircle, AlertTriangle, Wifi, Globe, Server, Info, ChevronDown } from 'lucide-react';
import { Card } from '../../ui/Card';
import type { NetworkDiagnostics } from '@services/api.service';

interface NetworkStatusSectionProps {
  diagnostics: NetworkDiagnostics | undefined;
}

function hasIpv6Resolution(result: NetworkDiagnostics['dnsResults'][number]) {
  if (!result.resolvedIps || result.resolvedIps.length === 0) {
    return false;
  }

  return result.resolvedIps.some(ip => ip.includes(':'));
}

export function NetworkStatusSection({ diagnostics }: NetworkStatusSectionProps) {
  const { t } = useTranslation();
  const [isExpanded, setIsExpanded] = useState(false);

  if (!diagnostics) {
    return null;
  }

  // With host networking, public DNS IPs are expected - steam-prefill detects lancache via localhost/gateway
  const hasPublicDnsWithHostNetworking = diagnostics.useHostNetworking &&
    diagnostics.dnsResults.some(r => r.success && !r.isPrivateIp);

  const hasIpv6BypassIssue = !diagnostics.useHostNetworking &&
    diagnostics.dnsResults.some(r => r.success && hasIpv6Resolution(r) && !r.isPrivateIp);

  // Only flag as issue if: no internet, DNS failed, public IP without host networking, or IPv6 bypass risk
  const hasRealIssue = !diagnostics.internetConnectivity ||
    diagnostics.dnsResults.some(r => !r.success) ||
    (!diagnostics.useHostNetworking && diagnostics.dnsResults.some(r => r.success && !r.isPrivateIp)) ||
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
            className="w-10 h-10 rounded-lg flex items-center justify-center flex-shrink-0"
            style={{ backgroundColor: statusBgColor }}
          >
            <Wifi className="h-5 w-5" style={{ color: statusColor }} />
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
            <div className="ml-1 text-xs p-2 rounded bg-[var(--theme-info-bg)] text-[var(--theme-info-text)]">
              {t('prefill.network.hostNetworkingHint')}
            </div>
          )}
          {/* Internet Connectivity */}
          <div className="flex items-center gap-2">
            {diagnostics.internetConnectivity ? (
              <CheckCircle2 className="h-4 w-4 flex-shrink-0 text-[var(--theme-success)]" />
            ) : (
              <XCircle className="h-4 w-4 flex-shrink-0 text-[var(--theme-error)]" />
            )}
            <Globe className="h-4 w-4 text-themed-muted flex-shrink-0" />
            <span className="text-sm text-themed-primary">{t('prefill.network.internetConnectivity')}</span>
            {diagnostics.internetConnectivity ? (
              <span className="text-xs text-themed-muted ml-auto">{t('prefill.network.ok')}</span>
            ) : (
              <span className="text-xs ml-auto text-[var(--theme-error)]">
                {t('prefill.network.failed')}
              </span>
            )}
          </div>

          {/* IPv4 Connectivity */}
          {diagnostics.internetConnectivityIpv4 !== undefined && (
            <div className="flex items-center gap-2">
              {diagnostics.internetConnectivityIpv4 === true ? (
                <CheckCircle2 className="h-4 w-4 flex-shrink-0 text-[var(--theme-success)]" />
              ) : diagnostics.internetConnectivityIpv4 === false ? (
                <XCircle className="h-4 w-4 flex-shrink-0 text-[var(--theme-error)]" />
              ) : (
                <Info className="h-4 w-4 flex-shrink-0 text-[var(--theme-info)]" />
              )}
              <Globe className="h-4 w-4 text-themed-muted flex-shrink-0" />
              <span className="text-sm text-themed-primary">{t('prefill.network.ipv4Connectivity')}</span>
              {diagnostics.internetConnectivityIpv4 === true ? (
                <span className="text-xs text-themed-muted ml-auto">{t('prefill.network.ok')}</span>
              ) : diagnostics.internetConnectivityIpv4 === false ? (
                <span className="text-xs ml-auto text-[var(--theme-error)]">
                  {t('prefill.network.failed')}
                </span>
              ) : (
                <span className="text-xs ml-auto text-[var(--theme-info)]">
                  {t('prefill.network.notTested')}
                </span>
              )}
            </div>
          )}

          {/* IPv6 Connectivity */}
          {diagnostics.internetConnectivityIpv6 !== undefined && (
            <div className="flex items-center gap-2">
              {diagnostics.internetConnectivityIpv6 === true ? (
                <CheckCircle2 className="h-4 w-4 flex-shrink-0 text-[var(--theme-success)]" />
              ) : diagnostics.internetConnectivityIpv6 === false ? (
                <XCircle className="h-4 w-4 flex-shrink-0 text-[var(--theme-error)]" />
              ) : (
                <Info className="h-4 w-4 flex-shrink-0 text-[var(--theme-info)]" />
              )}
              <Globe className="h-4 w-4 text-themed-muted flex-shrink-0" />
              <span className="text-sm text-themed-primary">{t('prefill.network.ipv6Connectivity')}</span>
              {diagnostics.internetConnectivityIpv6 === true ? (
                <span className="text-xs text-themed-muted ml-auto">{t('prefill.network.ok')}</span>
              ) : diagnostics.internetConnectivityIpv6 === false ? (
                <span className="text-xs ml-auto text-[var(--theme-error)]">
                  {t('prefill.network.failed')}
                </span>
              ) : (
                <span className="text-xs ml-auto text-[var(--theme-info)]">
                  {t('prefill.network.notTested')}
                </span>
              )}
            </div>
          )}

          {/* Internet Error Details */}
          {!diagnostics.internetConnectivity && diagnostics.internetConnectivityError && (
            <div className="ml-6 text-xs p-2 rounded bg-[var(--theme-error-bg)] text-[var(--theme-error-text)]">
              {diagnostics.internetConnectivityError}
              <div className="mt-1 text-themed-muted">
                {t('prefill.network.trySetting')} <code className="px-1 py-0.5 rounded bg-themed-tertiary">Prefill__NetworkMode=bridge</code> {t('prefill.network.inDockerCompose')}
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
                    className={`text-xs ml-auto font-mono ${
                      result.isPrivateIp
                        ? 'text-[var(--theme-success)]'
                        : diagnostics.useHostNetworking
                          ? 'text-[var(--theme-info)]'
                          : 'text-[var(--theme-warning)]'
                    }`}
                  >
                    {(result.resolvedIps && result.resolvedIps.length > 0
                      ? result.resolvedIps.join(', ')
                      : '')}
                  </span>
                ) : (
                  <span className="text-xs ml-auto text-[var(--theme-error)]">
                    {t('prefill.network.notResolved')}
                  </span>
                )}
              </div>

              {/* DNS Info/Warning for public IP - different message based on host networking */}
              {result.success && !result.isPrivateIp && (
                <div
                  className={`ml-6 text-xs p-2 rounded ${
                    diagnostics.useHostNetworking
                      ? 'bg-[var(--theme-info-bg)] text-[var(--theme-info-text)]'
                      : 'bg-[var(--theme-warning-bg)] text-[var(--theme-warning-text)]'
                  }`}
                >
                  {diagnostics.useHostNetworking ? (
                    <>
                      {t('prefill.network.publicDnsExpected')}
                    </>
                  ) : (
                    <>
                      {t('prefill.network.publicIpDetected')}
                    </>
                  )}
                </div>
              )}

              {/* IPv6 bypass warning */}
              {result.success && hasIpv6Resolution(result) && !result.isPrivateIp && !diagnostics.useHostNetworking && (
                <div className="ml-6 text-xs p-2 rounded bg-[var(--theme-warning-bg)] text-[var(--theme-warning-text)]">
                  {t('prefill.network.ipv6BypassDetected')}
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

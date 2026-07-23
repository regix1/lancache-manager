import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle2, XCircle, AlertTriangle, Wifi, Info, ChevronDown } from 'lucide-react';
import { Card } from '../../ui/Card';
import { CollapsibleRegion } from '../../ui/CollapsibleRegion';
import { Tooltip } from '../../ui/Tooltip';
import type { NetworkDiagnostics } from '@services/api.service';

interface NetworkStatusSectionProps {
  diagnostics: NetworkDiagnostics | undefined;
}

interface HintDetailsProps {
  children: ReactNode;
}

/* Long informational copy folds behind this disclosure so the check rows keep their
   rhythm; warnings and errors always render in place instead. [21] */
function HintDetails({ children }: HintDetailsProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);

  return (
    <div>
      <button
        type="button"
        onClick={() => setOpen(!open)}
        aria-expanded={open}
        className="focus-ring prefill-network-hint-toggle"
      >
        <span>{t('prefill.network.details', 'Details')}</span>
        <ChevronDown
          className={`h-3.5 w-3.5 transition-transform duration-200 ${open ? 'rotate-180' : ''}`}
        />
      </button>
      <CollapsibleRegion open={open}>{children}</CollapsibleRegion>
    </div>
  );
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

  const statusBoxClass = hasRealIssue
    ? 'prefill-network-status-box--warning'
    : showInfoState
      ? 'prefill-network-status-box--info'
      : 'prefill-network-status-box--success';

  // When the shared locator auto-detected (and heartbeat-verified) a cache the user did not configure
  // by hand, report the source positively instead of the generic "resolution failed" warning.
  const lancacheIpSource = diagnostics.lancacheIpSource;
  const isAutoDetectedLancache =
    !!diagnostics.lancacheIpInjected && !!lancacheIpSource && lancacheIpSource !== 'config';
  const lancacheSourceLabel =
    lancacheIpSource === 'dns'
      ? t('prefill.network.lancacheSourceDns')
      : lancacheIpSource === 'dockerInspect'
        ? t('prefill.network.lancacheSourceDockerInspect')
        : lancacheIpSource === 'envFile'
          ? t('prefill.network.lancacheSourceEnvFile')
          : t('prefill.network.lancacheSourceDetected');

  return (
    <Card padding="md">
      <div className="space-y-3">
        {/* Clickable Header */}
        <button
          onClick={() => setIsExpanded(!isExpanded)}
          className="flex items-center gap-3 w-full p-2 min-h-[44px] text-left rounded-lg transition-[background-color] duration-150 hover:bg-[var(--theme-bg-hover)] focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--theme-border-focus)]"
        >
          <div
            className={`w-10 h-10 rounded-lg flex items-center justify-center flex-shrink-0 ${statusBoxClass}`}
          >
            <Wifi className="h-5 w-5 prefill-network-status-icon" />
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
        <CollapsibleRegion
          open={isExpanded}
          contentClassName="space-y-3 pt-2 border-t border-themed-secondary"
        >
          {/* Cache Routing */}
          <div className="well-surface prefill-network-well">
            <p className="caps-label prefill-network-well-title">
              {t('prefill.network.groupCacheRouting', 'Cache routing')}
            </p>
            {/* Lancache IP injected (Prefill__LancacheIp) */}
            {diagnostics.lancacheIpInjected ? (
              <div className="flex items-center gap-2 flex-wrap">
                <CheckCircle2 className="h-4 w-4 flex-shrink-0 text-[var(--theme-success)]" />
                <span className="text-sm text-themed-primary">
                  {isAutoDetectedLancache
                    ? t('prefill.network.lancacheIpDetected', {
                        ip: diagnostics.lancacheIpInjected,
                        source: lancacheSourceLabel
                      })
                    : t('prefill.network.lancacheIpInjected', {
                        ip: diagnostics.lancacheIpInjected
                      })}
                </span>
              </div>
            ) : (
              <div className="text-xs p-2.5 rounded bg-[var(--theme-warning-bg)] text-[var(--theme-warning-text)] leading-relaxed">
                {t('prefill.network.lancacheIpResolutionFailed')}
              </div>
            )}
            {/* Host networking hint */}
            {diagnostics.useHostNetworking && (
              <HintDetails>
                <div className="mt-1 text-xs p-2.5 rounded bg-[var(--theme-info-bg)] text-[var(--theme-info-text)] leading-relaxed">
                  {t('prefill.network.hostNetworkingHint')}
                </div>
              </HintDetails>
            )}
          </div>

          {/* Internet Connectivity */}
          <div className="well-surface prefill-network-well">
            <p className="caps-label prefill-network-well-title">
              {t('prefill.network.groupInternet', 'Internet connectivity')}
            </p>
            <div className="flex items-center justify-between gap-2 flex-wrap">
              <div className="flex items-center gap-2">
                {diagnostics.internetConnectivity ? (
                  <CheckCircle2 className="h-4 w-4 flex-shrink-0 text-[var(--theme-success)]" />
                ) : (
                  <XCircle className="h-4 w-4 flex-shrink-0 text-[var(--theme-error)]" />
                )}
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
              <HintDetails>
                <div className="mt-1 text-xs p-2.5 rounded leading-relaxed bg-[var(--theme-info-bg)] text-[var(--theme-info-text)]">
                  {t('prefill.network.ipv6UnsupportedHint')}
                </div>
              </HintDetails>
            )}

            {/* Internet Error Details */}
            {!diagnostics.internetConnectivity && diagnostics.internetConnectivityError && (
              <>
                <div className="text-xs p-2.5 rounded leading-relaxed bg-[var(--theme-error-bg)] text-[var(--theme-error-text)]">
                  {diagnostics.internetConnectivityError}
                </div>
                <HintDetails>
                  <div className="mt-1 text-xs p-2.5 rounded leading-relaxed bg-[var(--theme-info-bg)] text-[var(--theme-info-text)]">
                    {t('prefill.network.trySetting')}{' '}
                    <code className="px-1 py-0.5 rounded bg-themed-tertiary break-all">
                      Prefill__NetworkMode=bridge
                    </code>{' '}
                    {t('prefill.network.inDockerCompose')}
                  </div>
                </HintDetails>
              </>
            )}
          </div>

          {/* DNS Resolution */}
          <div className="well-surface prefill-network-well">
            <p className="caps-label prefill-network-well-title">
              {t('prefill.network.groupDns', 'DNS resolution')}
            </p>
            <div className="divided-list">
              {diagnostics.dnsResults.map((result, index) => {
                const isPrimaryDomain = result.domain === primaryDomain;
                // For secondary domains, show info style if primary domain is configured
                // For the primary domain, only show info with host networking
                const showAsInfo =
                  diagnostics.useHostNetworking || (!isPrimaryDomain && hasPrimaryConfigured);

                return (
                  <div key={index} className="space-y-1 py-1.5 first:pt-0 last:pb-0">
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
                        <Tooltip content={result.domain} position="top" className="flex min-w-0">
                          <span className="text-sm text-themed-primary break-all">
                            {result.domain}
                          </span>
                        </Tooltip>
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

                    {/* Public-IP message: informational variants fold behind Details,
                        warning variants stay visible on the domain they belong to */}
                    {result.success &&
                      !result.isPrivateIp &&
                      (showAsInfo ? (
                        <HintDetails>
                          <div className="mt-1 text-xs p-2.5 rounded leading-relaxed bg-[var(--theme-info-bg)] text-[var(--theme-info-text)]">
                            {diagnostics.useHostNetworking
                              ? t('prefill.network.publicDnsExpected')
                              : t('prefill.network.optionalDomainNotConfigured')}
                          </div>
                        </HintDetails>
                      ) : (
                        <div className="text-xs p-2.5 rounded leading-relaxed bg-[var(--theme-warning-bg)] text-[var(--theme-warning-text)]">
                          {isPrimaryDomain
                            ? t('prefill.network.primaryDomainNotConfigured')
                            : t('prefill.network.publicIpDetected')}
                        </div>
                      ))}

                    {/* IPv6 bypass warning - only show if primary domain not configured */}
                    {result.success &&
                      hasIpv6Resolution(result) &&
                      !result.isPrivateIp &&
                      !diagnostics.useHostNetworking &&
                      !hasPrimaryConfigured && (
                        <div className="text-xs p-2.5 rounded leading-relaxed bg-[var(--theme-warning-bg)] text-[var(--theme-warning-text)]">
                          {t('prefill.network.ipv6BypassDetected')}
                        </div>
                      )}

                    {/* DNS Error Details */}
                    {!result.success && result.error && (
                      <div className="text-xs p-2.5 rounded leading-relaxed bg-[var(--theme-error-bg)] text-[var(--theme-error-text)]">
                        {result.error}
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </div>
        </CollapsibleRegion>
      </div>
    </Card>
  );
}

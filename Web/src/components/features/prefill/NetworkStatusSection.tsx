import { CheckCircle2, XCircle, AlertTriangle, Wifi, Globe, Server } from 'lucide-react';
import { Card } from '../../ui/Card';
import type { NetworkDiagnostics } from '@services/api.service';

interface NetworkStatusSectionProps {
  diagnostics: NetworkDiagnostics | undefined;
}

export function NetworkStatusSection({ diagnostics }: NetworkStatusSectionProps) {
  if (!diagnostics) {
    return null;
  }

  const hasAnyIssue = !diagnostics.internetConnectivity ||
    diagnostics.dnsResults.some(r => !r.success || !r.isPrivateIp);

  return (
    <Card padding="md">
      <div className="space-y-3">
        {/* Header */}
        <div className="flex items-center gap-3">
          <div
            className="w-10 h-10 rounded-lg flex items-center justify-center"
            style={{
              backgroundColor: hasAnyIssue
                ? 'color-mix(in srgb, var(--theme-warning) 15%, transparent)'
                : 'color-mix(in srgb, var(--theme-success) 15%, transparent)'
            }}
          >
            <Wifi
              className="h-5 w-5"
              style={{ color: hasAnyIssue ? 'var(--theme-warning)' : 'var(--theme-success)' }}
            />
          </div>
          <div>
            <p className="font-medium text-themed-primary">Container Network Status</p>
            <p className="text-sm text-themed-muted">
              {hasAnyIssue ? 'Some issues detected' : 'All checks passed'}
            </p>
          </div>
        </div>

        {/* Status Items */}
        <div className="space-y-2 pl-1">
          {/* Internet Connectivity */}
          <div className="flex items-center gap-2">
            {diagnostics.internetConnectivity ? (
              <CheckCircle2 className="h-4 w-4 flex-shrink-0" style={{ color: 'var(--theme-success)' }} />
            ) : (
              <XCircle className="h-4 w-4 flex-shrink-0" style={{ color: 'var(--theme-error)' }} />
            )}
            <Globe className="h-4 w-4 text-themed-muted flex-shrink-0" />
            <span className="text-sm text-themed-primary">Internet connectivity</span>
            {diagnostics.internetConnectivity ? (
              <span className="text-xs text-themed-muted ml-auto">OK</span>
            ) : (
              <span className="text-xs ml-auto" style={{ color: 'var(--theme-error)' }}>
                Failed
              </span>
            )}
          </div>

          {/* Internet Error Details */}
          {!diagnostics.internetConnectivity && diagnostics.internetConnectivityError && (
            <div
              className="ml-6 text-xs p-2 rounded"
              style={{
                backgroundColor: 'var(--theme-error-bg)',
                color: 'var(--theme-error-text)'
              }}
            >
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
                    <CheckCircle2 className="h-4 w-4 flex-shrink-0" style={{ color: 'var(--theme-success)' }} />
                  ) : (
                    <AlertTriangle className="h-4 w-4 flex-shrink-0" style={{ color: 'var(--theme-warning)' }} />
                  )
                ) : (
                  <XCircle className="h-4 w-4 flex-shrink-0" style={{ color: 'var(--theme-error)' }} />
                )}
                <Server className="h-4 w-4 text-themed-muted flex-shrink-0" />
                <span className="text-sm text-themed-primary truncate" title={result.domain}>
                  {result.domain}
                </span>
                {result.success ? (
                  <span
                    className="text-xs ml-auto font-mono"
                    style={{
                      color: result.isPrivateIp ? 'var(--theme-success)' : 'var(--theme-warning)'
                    }}
                  >
                    {result.resolvedIp}
                  </span>
                ) : (
                  <span className="text-xs ml-auto" style={{ color: 'var(--theme-error)' }}>
                    Not resolved
                  </span>
                )}
              </div>

              {/* DNS Warning for public IP */}
              {result.success && !result.isPrivateIp && (
                <div
                  className="ml-6 text-xs p-2 rounded"
                  style={{
                    backgroundColor: 'var(--theme-warning-bg)',
                    color: 'var(--theme-warning-text)'
                  }}
                >
                  Public IP detected - lancache-dns may not be configured.
                  Prefill may download from internet instead of populating cache.
                </div>
              )}

              {/* DNS Error Details */}
              {!result.success && result.error && (
                <div
                  className="ml-6 text-xs p-2 rounded"
                  style={{
                    backgroundColor: 'var(--theme-error-bg)',
                    color: 'var(--theme-error-text)'
                  }}
                >
                  {result.error}
                </div>
              )}
            </div>
          ))}
        </div>
      </div>
    </Card>
  );
}

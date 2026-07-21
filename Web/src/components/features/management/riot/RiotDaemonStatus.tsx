import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle, XCircle } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { RiotIcon } from '@components/ui/RiotIcon';
import { LoadingState } from '@components/ui/ManagerCard';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import ApiService from '@services/api.service';
import type { EpicDaemonStatusDto } from '../../../../types';

// Riot prefill is fully anonymous (no account login). This card only reports
// daemon connectivity (Docker availability + active session count) — there is no
// login flow, unlike EpicDaemonStatus. Status is read from /api/riot-daemon
// and refreshed live via the /riot-prefill-daemon hub events.

interface RiotDaemonStatusProps {
  onError?: (message: string) => void;
}

const RiotDaemonStatus: React.FC<RiotDaemonStatusProps> = ({ onError }) => {
  const { t } = useTranslation();
  const { on, off, connectionState } = useSignalR();
  const [status, setStatus] = useState<EpicDaemonStatusDto | null>(null);
  const [hasError, setHasError] = useState(false);
  const [loading, setLoading] = useState(true);

  const loadStatus = useCallback(async () => {
    try {
      const data = await ApiService.getRiotDaemonStatus();
      setStatus(data);
      setHasError(false);
    } catch {
      setHasError(true);
      setStatus({
        dockerAvailable: false,
        activeSessions: 0,
        maxSessionsPerUser: 1,
        sessionTimeoutMinutes: 120
      });
      onError?.(
        t(
          'management.sections.integrations.riotDaemonStatus.loadError',
          'Failed to load Riot status. Displaying default values.'
        )
      );
    }
  }, [onError, t]);

  useEffect(() => {
    loadStatus().finally(() => setLoading(false));
  }, [loadStatus]);

  // Refresh when the daemon reports a status change over the Riot hub
  useEffect(() => {
    const handleUpdate = () => {
      loadStatus();
    };
    on('RiotStatusChanged', handleUpdate);
    on('RiotDaemonSessionCreated', handleUpdate);
    on('RiotDaemonSessionTerminated', handleUpdate);
    return () => {
      off('RiotStatusChanged', handleUpdate);
      off('RiotDaemonSessionCreated', handleUpdate);
      off('RiotDaemonSessionTerminated', handleUpdate);
    };
  }, [on, off, loadStatus]);

  // Refresh data when SignalR reconnects (catches events missed during disconnect)
  useEffect(() => {
    if (connectionState === 'connected') {
      loadStatus();
    }
  }, [connectionState, loadStatus]);

  const isReady = status?.dockerAvailable ?? false;
  const activeSessions = status?.activeSessions ?? 0;

  return (
    <Card>
      {/* Header: Riot icon + Title + HelpPopover */}
      <div className="flex items-center gap-3 mb-4">
        <div className="w-10 h-10 rounded-lg flex items-center justify-center bg-[var(--theme-riot-subtle)] text-[var(--theme-riot)]">
          <RiotIcon size={20} />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary">
          {t('management.sections.integrations.riotDaemonStatus.title', 'Riot Games')}
        </h3>
        <HelpPopover position="left" width={320}>
          <HelpSection
            title={t(
              'management.sections.integrations.riotDaemonStatus.help.anonymous.title',
              'Anonymous Prefill'
            )}
            variant="subtle"
          >
            <HelpDefinition
              items={[
                {
                  term: t(
                    'management.sections.integrations.riotDaemonStatus.help.anonymous.noLogin.term',
                    'No Account Login'
                  ),
                  description: t(
                    'management.sections.integrations.riotDaemonStatus.help.anonymous.noLogin.description',
                    'Riot prefill downloads public Riot CDN content and requires no account, credentials, or login.'
                  )
                },
                {
                  term: t(
                    'management.sections.integrations.riotDaemonStatus.help.anonymous.products.term',
                    'Product Catalog'
                  ),
                  description: t(
                    'management.sections.integrations.riotDaemonStatus.help.anonymous.products.description',
                    'The daemon exposes the full fixed Riot product catalog for prefill selection.'
                  )
                }
              ]}
            />
          </HelpSection>
          <HelpNote type="info">
            {t(
              'management.sections.integrations.riotDaemonStatus.help.note',
              'No login required. The daemon container only needs Docker to be available to run prefill sessions.'
            )}
          </HelpNote>
        </HelpPopover>
        {!loading && (
          <div className="ml-auto flex-shrink-0">
            {isReady ? (
              <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-success text-themed-success">
                <CheckCircle size={14} />
                {t('management.sections.integrations.riotDaemonStatus.connected', 'Connected')}
              </span>
            ) : (
              <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-secondary text-themed-muted">
                <XCircle size={14} />
                {t(
                  'management.sections.integrations.riotDaemonStatus.notConnected',
                  'Not Connected'
                )}
              </span>
            )}
          </div>
        )}
      </div>

      {loading ? (
        <LoadingState
          message={t(
            'management.sections.integrations.riotDaemonStatus.loadingStatus',
            'Loading Riot status...'
          )}
          rows={1}
        />
      ) : (
        <>
          {/* Error Warning */}
          {hasError && (
            <div className="p-2 mb-2 rounded-lg bg-themed-warning text-themed-warning text-xs">
              {t(
                'management.sections.integrations.riotDaemonStatus.loadError',
                'Failed to load Riot status. Displaying default values.'
              )}
            </div>
          )}

          {/* Status Row */}
          <div className="p-3 rounded-lg bg-themed-tertiary">
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
              <div className="flex-1 min-w-0">
                <p className="text-themed-primary text-sm font-medium mb-1">
                  {isReady
                    ? t(
                        'management.sections.integrations.riotDaemonStatus.dockerStatus',
                        'Docker Service'
                      )
                    : t(
                        'management.sections.integrations.riotDaemonStatus.notConnected',
                        'Not Connected'
                      )}
                </p>
                <p className="text-xs text-themed-muted">
                  {isReady
                    ? t(
                        'management.sections.integrations.riotDaemonStatus.dockerAvailableDesc',
                        'Docker is available and ready for Riot prefill sessions. No account login required.'
                      )
                    : t(
                        'management.sections.integrations.riotDaemonStatus.dockerUnavailableDesc',
                        'Start Docker to enable Riot prefill sessions.'
                      )}
                </p>
              </div>
              <div className="flex-shrink-0">
                <span className="text-xs text-themed-muted">
                  {activeSessions > 0
                    ? t('management.sections.integrations.riotDaemonStatus.activeSessions', {
                        count: activeSessions,
                        defaultValue: '{{count}} active session'
                      })
                    : t(
                        'management.sections.integrations.riotDaemonStatus.noActiveSessions',
                        'No active sessions'
                      )}
                </span>
              </div>
            </div>
          </div>
        </>
      )}
    </Card>
  );
};

export default RiotDaemonStatus;

import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle, XCircle } from 'lucide-react';
import { AccordionSection } from '@components/ui/AccordionSection';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import { LoadingState } from '@components/ui/ManagerCard';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import ApiService from '@services/api.service';
import type { EpicDaemonStatusDto } from '../../../../types';

// Battle.net prefill is fully anonymous (no account login). This card only reports
// daemon connectivity (Docker availability + active session count) — there is no
// login flow, unlike EpicDaemonStatus. Status is read from /api/battlenet-daemon
// and refreshed live via the /battlenet-prefill-daemon hub events.

interface BattleNetDaemonStatusProps {
  onError?: (message: string) => void;
}

const BattleNetDaemonStatus: React.FC<BattleNetDaemonStatusProps> = ({ onError }) => {
  const { t } = useTranslation();
  const { on, off, connectionState } = useSignalR();
  const [status, setStatus] = useState<EpicDaemonStatusDto | null>(null);
  const [hasError, setHasError] = useState(false);
  const [loading, setLoading] = useState(true);
  const [expanded, setExpanded] = useState(false);

  const loadStatus = useCallback(async () => {
    try {
      const data = await ApiService.getBattleNetDaemonStatus();
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
          'management.sections.integrations.battlenetDaemonStatus.loadError',
          'Failed to load Battle.net status. Displaying default values.'
        )
      );
    }
  }, [onError, t]);

  useEffect(() => {
    loadStatus().finally(() => setLoading(false));
  }, [loadStatus]);

  // Refresh when the daemon reports a status change over the Battle.net hub
  useEffect(() => {
    const handleUpdate = () => {
      loadStatus();
    };
    on('BattleNetStatusChanged', handleUpdate);
    on('BattleNetDaemonSessionCreated', handleUpdate);
    on('BattleNetDaemonSessionTerminated', handleUpdate);
    return () => {
      off('BattleNetStatusChanged', handleUpdate);
      off('BattleNetDaemonSessionCreated', handleUpdate);
      off('BattleNetDaemonSessionTerminated', handleUpdate);
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

  const statusBadge = !loading ? (
    isReady ? (
      <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-success text-themed-success">
        <CheckCircle size={14} />
        {t('management.sections.integrations.battlenetDaemonStatus.connected', 'Connected')}
      </span>
    ) : (
      <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-secondary text-themed-muted">
        <XCircle size={14} />
        {t('management.sections.integrations.battlenetDaemonStatus.notConnected', 'Not Connected')}
      </span>
    )
  ) : undefined;

  const helpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection
        title={t(
          'management.sections.integrations.battlenetDaemonStatus.help.anonymous.title',
          'Anonymous Prefill'
        )}
        variant="subtle"
      >
        <HelpDefinition
          items={[
            {
              term: t(
                'management.sections.integrations.battlenetDaemonStatus.help.anonymous.noLogin.term',
                'No Account Login'
              ),
              description: t(
                'management.sections.integrations.battlenetDaemonStatus.help.anonymous.noLogin.description',
                'Battle.net prefill downloads public Blizzard CDN content and requires no account, credentials, or login.'
              )
            },
            {
              term: t(
                'management.sections.integrations.battlenetDaemonStatus.help.anonymous.products.term',
                'Product Catalog'
              ),
              description: t(
                'management.sections.integrations.battlenetDaemonStatus.help.anonymous.products.description',
                'The daemon exposes the full fixed Battle.net product catalog for prefill selection.'
              )
            }
          ]}
        />
      </HelpSection>
      <HelpNote type="info">
        {t(
          'management.sections.integrations.battlenetDaemonStatus.help.note',
          'No login required. The daemon container only needs Docker to be available to run prefill sessions.'
        )}
      </HelpNote>
    </HelpPopover>
  );

  return (
    <AccordionSection
      title={t('management.sections.integrations.battlenetDaemonStatus.title', 'Battle.net')}
      description={t('management.sections.integrations.battlenetDaemonStatus.summary')}
      titleAccessory={helpAccessory}
      icon={BlizzardIcon}
      iconColor="var(--theme-blizzard)"
      isExpanded={expanded}
      onToggle={() => setExpanded((prev) => !prev)}
      badge={statusBadge}
    >
      {loading ? (
        <LoadingState
          message={t(
            'management.sections.integrations.battlenetDaemonStatus.loadingStatus',
            'Loading Battle.net status...'
          )}
          rows={1}
        />
      ) : (
        <>
          {hasError && (
            <div className="p-2 mb-2 rounded-lg bg-themed-warning text-themed-warning text-xs">
              {t(
                'management.sections.integrations.battlenetDaemonStatus.loadError',
                'Failed to load Battle.net status. Displaying default values.'
              )}
            </div>
          )}

          <div className="p-3 rounded-lg bg-themed-tertiary">
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
              <div className="flex-1 min-w-0">
                <p className="text-themed-primary text-sm font-medium mb-1">
                  {isReady
                    ? t(
                        'management.sections.integrations.battlenetDaemonStatus.dockerStatus',
                        'Docker Service'
                      )
                    : t(
                        'management.sections.integrations.battlenetDaemonStatus.notConnected',
                        'Not Connected'
                      )}
                </p>
                <p className="text-xs text-themed-muted">
                  {isReady
                    ? t(
                        'management.sections.integrations.battlenetDaemonStatus.dockerAvailableDesc',
                        'Docker is available and ready for Battle.net prefill sessions. No account login required.'
                      )
                    : t(
                        'management.sections.integrations.battlenetDaemonStatus.dockerUnavailableDesc',
                        'Start Docker to enable Battle.net prefill sessions.'
                      )}
                </p>
              </div>
              <div className="flex-shrink-0">
                <span className="text-xs text-themed-muted">
                  {activeSessions > 0
                    ? t('management.sections.integrations.battlenetDaemonStatus.activeSessions', {
                        count: activeSessions,
                        defaultValue: '{{count}} active session'
                      })
                    : t(
                        'management.sections.integrations.battlenetDaemonStatus.noActiveSessions',
                        'No active sessions'
                      )}
                </span>
              </div>
            </div>
          </div>
        </>
      )}
    </AccordionSection>
  );
};

export default BattleNetDaemonStatus;

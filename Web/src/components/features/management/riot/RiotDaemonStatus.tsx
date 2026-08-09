import React from 'react';
import { useTranslation } from 'react-i18next';
import { RiotIcon } from '@components/ui/RiotIcon';
import ApiService from '@services/api.service';
import AnonymousDaemonStatus from '../daemon-status/AnonymousDaemonStatus';
import type { AnonymousDaemonService } from '../daemon-status/daemonStatus.types';

// Riot prefill is fully anonymous (no account login), so this card uses the login-free variant of
// the shared daemon card: it reports daemon connectivity (Docker availability + active session
// count) and has no login flow, unlike EpicDaemonStatus. Status is read from /api/riot-daemon and
// refreshed live via the /riot-prefill-daemon hub events.

// Module-level so the identity is stable: it drives the status fetch and the hub subscriptions.
const RIOT_DAEMON: AnonymousDaemonService = {
  integrationKey: 'riot',
  accordionId: 'integrations-riot',
  icon: RiotIcon,
  iconColor: 'var(--theme-riot)',
  loadStatus: () => ApiService.getRiotDaemonStatus(),
  refreshEvents: ['RiotStatusChanged', 'RiotDaemonSessionCreated', 'RiotDaemonSessionTerminated']
};

interface RiotDaemonStatusProps {
  onError?: (message: string) => void;
}

const RiotDaemonStatus: React.FC<RiotDaemonStatusProps> = ({ onError }) => {
  const { t } = useTranslation();

  return (
    <AnonymousDaemonStatus
      service={RIOT_DAEMON}
      onError={onError}
      copy={{
        title: t('management.sections.integrations.riotDaemonStatus.title'),
        summary: t('management.sections.integrations.riotDaemonStatus.summary'),
        connected: t('management.sections.integrations.riotDaemonStatus.connected'),
        notConnected: t('management.sections.integrations.riotDaemonStatus.notConnected'),
        loadingStatus: t('management.sections.integrations.riotDaemonStatus.loadingStatus'),
        loadError: t('management.sections.integrations.riotDaemonStatus.loadError'),
        availableHeadline: t('management.sections.integrations.riotDaemonStatus.dockerStatus'),
        availableDetail: t('management.sections.integrations.riotDaemonStatus.dockerAvailableDesc'),
        unavailableDetail: t(
          'management.sections.integrations.riotDaemonStatus.dockerUnavailableDesc'
        ),
        sessionCount: (count) =>
          count > 0
            ? t('management.sections.integrations.riotDaemonStatus.activeSessions', {
                count,
                defaultValue: '{{count}} active session'
              })
            : t('management.sections.integrations.riotDaemonStatus.noActiveSessions'),
        help: {
          title: t('management.sections.integrations.riotDaemonStatus.help.anonymous.title'),
          definitions: [
            {
              term: t(
                'management.sections.integrations.riotDaemonStatus.help.anonymous.noLogin.term'
              ),
              description: t(
                'management.sections.integrations.riotDaemonStatus.help.anonymous.noLogin.description'
              )
            },
            {
              term: t(
                'management.sections.integrations.riotDaemonStatus.help.anonymous.products.term'
              ),
              description: t(
                'management.sections.integrations.riotDaemonStatus.help.anonymous.products.description'
              )
            }
          ],
          note: t('management.sections.integrations.riotDaemonStatus.help.note')
        }
      }}
    />
  );
};

export default RiotDaemonStatus;

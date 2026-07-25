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
        title: t('management.sections.integrations.riotDaemonStatus.title', 'Riot Games'),
        summary: t('management.sections.integrations.riotDaemonStatus.summary'),
        connected: t('management.sections.integrations.riotDaemonStatus.connected', 'Connected'),
        notConnected: t(
          'management.sections.integrations.riotDaemonStatus.notConnected',
          'Not Connected'
        ),
        loadingStatus: t(
          'management.sections.integrations.riotDaemonStatus.loadingStatus',
          'Loading Riot status...'
        ),
        loadError: t(
          'management.sections.integrations.riotDaemonStatus.loadError',
          'Failed to load Riot status. Displaying default values.'
        ),
        availableHeadline: t(
          'management.sections.integrations.riotDaemonStatus.dockerStatus',
          'Docker Service'
        ),
        availableDetail: t(
          'management.sections.integrations.riotDaemonStatus.dockerAvailableDesc',
          'Docker is available and ready for Riot prefill sessions. No account login required.'
        ),
        unavailableDetail: t(
          'management.sections.integrations.riotDaemonStatus.dockerUnavailableDesc',
          'Start Docker to enable Riot prefill sessions.'
        ),
        sessionCount: (count) =>
          count > 0
            ? t('management.sections.integrations.riotDaemonStatus.activeSessions', {
                count,
                defaultValue: '{{count}} active session'
              })
            : t(
                'management.sections.integrations.riotDaemonStatus.noActiveSessions',
                'No active sessions'
              ),
        help: {
          title: t(
            'management.sections.integrations.riotDaemonStatus.help.anonymous.title',
            'Anonymous Prefill'
          ),
          definitions: [
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
          ],
          note: t(
            'management.sections.integrations.riotDaemonStatus.help.note',
            'No login required. The daemon container only needs Docker to be available to run prefill sessions.'
          )
        }
      }}
    />
  );
};

export default RiotDaemonStatus;

import React from 'react';
import { useTranslation } from 'react-i18next';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import ApiService from '@services/api.service';
import AnonymousDaemonStatus from '../daemon-status/AnonymousDaemonStatus';
import type { AnonymousDaemonService } from '../daemon-status/daemonStatus.types';

// Battle.net prefill is fully anonymous (no account login), so this card uses the login-free variant
// of the shared daemon card: it reports daemon connectivity (Docker availability + active session
// count) and has no login flow, unlike EpicDaemonStatus. Status is read from /api/battlenet-daemon
// and refreshed live via the /battlenet-prefill-daemon hub events.

// Module-level so the identity is stable: it drives the status fetch and the hub subscriptions.
const BATTLENET_DAEMON: AnonymousDaemonService = {
  integrationKey: 'battlenet',
  accordionId: 'integrations-battlenet',
  icon: BlizzardIcon,
  iconColor: 'var(--theme-blizzard)',
  loadStatus: () => ApiService.getBattleNetDaemonStatus(),
  refreshEvents: [
    'BattleNetStatusChanged',
    'BattleNetDaemonSessionCreated',
    'BattleNetDaemonSessionTerminated'
  ]
};

interface BattleNetDaemonStatusProps {
  onError?: (message: string) => void;
}

const BattleNetDaemonStatus: React.FC<BattleNetDaemonStatusProps> = ({ onError }) => {
  const { t } = useTranslation();

  return (
    <AnonymousDaemonStatus
      service={BATTLENET_DAEMON}
      onError={onError}
      copy={{
        title: t('management.sections.integrations.battlenetDaemonStatus.title', 'Battle.net'),
        summary: t('management.sections.integrations.battlenetDaemonStatus.summary'),
        connected: t(
          'management.sections.integrations.battlenetDaemonStatus.connected',
          'Connected'
        ),
        notConnected: t(
          'management.sections.integrations.battlenetDaemonStatus.notConnected',
          'Not Connected'
        ),
        loadingStatus: t(
          'management.sections.integrations.battlenetDaemonStatus.loadingStatus',
          'Loading Battle.net status...'
        ),
        loadError: t(
          'management.sections.integrations.battlenetDaemonStatus.loadError',
          'Failed to load Battle.net status. Displaying default values.'
        ),
        availableHeadline: t(
          'management.sections.integrations.battlenetDaemonStatus.dockerStatus',
          'Docker Service'
        ),
        availableDetail: t(
          'management.sections.integrations.battlenetDaemonStatus.dockerAvailableDesc',
          'Docker is available and ready for Battle.net prefill sessions. No account login required.'
        ),
        unavailableDetail: t(
          'management.sections.integrations.battlenetDaemonStatus.dockerUnavailableDesc',
          'Start Docker to enable Battle.net prefill sessions.'
        ),
        sessionCount: (count) =>
          count > 0
            ? t('management.sections.integrations.battlenetDaemonStatus.activeSessions', {
                count,
                defaultValue: '{{count}} active session'
              })
            : t(
                'management.sections.integrations.battlenetDaemonStatus.noActiveSessions',
                'No active sessions'
              ),
        help: {
          title: t(
            'management.sections.integrations.battlenetDaemonStatus.help.anonymous.title',
            'Anonymous Prefill'
          ),
          definitions: [
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
          ],
          note: t(
            'management.sections.integrations.battlenetDaemonStatus.help.note',
            'No login required. The daemon container only needs Docker to be available to run prefill sessions.'
          )
        }
      }}
    />
  );
};

export default BattleNetDaemonStatus;

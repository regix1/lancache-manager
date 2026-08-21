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
  iconColor: '--theme-blizzard',
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
        title: t('management.sections.integrations.battlenetDaemonStatus.title'),
        summary: t('management.sections.integrations.battlenetDaemonStatus.summary'),
        connected: t('management.sections.integrations.battlenetDaemonStatus.connected'),
        notConnected: t('management.sections.integrations.battlenetDaemonStatus.notConnected'),
        loadingStatus: t('management.sections.integrations.battlenetDaemonStatus.loadingStatus'),
        loadError: t('management.sections.integrations.battlenetDaemonStatus.loadError'),
        availableHeadline: t('management.sections.integrations.battlenetDaemonStatus.dockerStatus'),
        availableDetail: t(
          'management.sections.integrations.battlenetDaemonStatus.dockerAvailableDesc'
        ),
        unavailableDetail: t(
          'management.sections.integrations.battlenetDaemonStatus.dockerUnavailableDesc'
        ),
        sessionCount: (count) =>
          count > 0
            ? t('management.sections.integrations.battlenetDaemonStatus.activeSessions', {
                count,
                defaultValue: '{{count}} active session'
              })
            : t('management.sections.integrations.battlenetDaemonStatus.noActiveSessions'),
        help: {
          title: t('management.sections.integrations.battlenetDaemonStatus.help.anonymous.title'),
          definitions: [
            {
              term: t(
                'management.sections.integrations.battlenetDaemonStatus.help.anonymous.noLogin.term'
              ),
              description: t(
                'management.sections.integrations.battlenetDaemonStatus.help.anonymous.noLogin.description'
              )
            },
            {
              term: t(
                'management.sections.integrations.battlenetDaemonStatus.help.anonymous.products.term'
              ),
              description: t(
                'management.sections.integrations.battlenetDaemonStatus.help.anonymous.products.description'
              )
            }
          ],
          note: t('management.sections.integrations.battlenetDaemonStatus.help.note')
        }
      }}
    />
  );
};

export default BattleNetDaemonStatus;

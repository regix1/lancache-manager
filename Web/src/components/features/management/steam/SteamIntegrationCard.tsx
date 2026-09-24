import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AccordionSection } from '@components/ui/AccordionSection';
import { HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import {
  SectionErrorChip,
  SectionHeaderActions,
  SectionHeaderChip
} from '@components/ui/SectionHeaderActions';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { SteamIcon } from '@components/ui/SteamIcon';
import { useSteamAuth } from '@contexts/useSteamAuth';
import { useSteamWebApiStatus } from '@contexts/useSteamWebApiStatus';
import { type AuthMode } from '@services/auth.service';
import SteamLoginManager from './SteamLoginManager';
import SteamWebApiStatus from './SteamWebApiStatus';
import '../managementSectionContent.css';
import './steamIntegration.css';

interface SteamIntegrationCardProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
}

const SteamIntegrationCard: React.FC<SteamIntegrationCardProps> = ({
  authMode,
  mockMode,
  onError,
  onSuccess
}) => {
  const { t } = useTranslation();
  const { steamAuthMode, error: loadError, refreshSteamAuth } = useSteamAuth();
  const { status, loading: webApiLoading, error: webApiError } = useSteamWebApiStatus();
  const [expanded, setExpanded] = useState(false);
  useAccordionGroupItem('integrations-steam', expanded, () => setExpanded((prev) => !prev));

  const isConnected = steamAuthMode === 'authenticated';

  const steamChip = isConnected ? (
    <SectionHeaderChip variant="success">{t('management.steamAuth.connected')}</SectionHeaderChip>
  ) : (
    <SectionHeaderChip variant="neutral">{t('management.steamAuth.anonymous')}</SectionHeaderChip>
  );

  const needsApiKey =
    status?.version === 'V1NoKey' || (status?.version === 'BothFailed' && !status?.hasApiKey);

  let webApiChip: React.ReactNode = null;
  if (status?.isFullyOperational) {
    webApiChip = (
      <SectionHeaderChip variant="success">
        {t('management.steamWebApi.badgeOperational')}
      </SectionHeaderChip>
    );
  } else if (status && needsApiKey) {
    webApiChip = (
      <SectionHeaderChip variant="warning">
        {t('management.steamWebApi.badgeNeedsKey')}
      </SectionHeaderChip>
    );
  } else if (status) {
    webApiChip = (
      <SectionHeaderChip variant="error">
        {t('management.steamWebApi.badgeUnavailable')}
      </SectionHeaderChip>
    );
  } else if (webApiLoading) {
    webApiChip = (
      <SectionHeaderChip variant="neutral">
        {t('management.steamWebApi.sectionTitle')}
      </SectionHeaderChip>
    );
  }

  const helpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.sections.integrations.steamCard.help.aboutTitle')}>
        {t('management.sections.integrations.steamCard.summary')}
      </HelpSection>
    </HelpPopover>
  );

  return (
    <AccordionSection
      title={t('management.sections.integrations.steamCard.title')}
      titleAccessory={helpAccessory}
      icon={SteamIcon}
      iconColor="--theme-steam"
      isExpanded={expanded}
      onToggle={() => setExpanded((prev) => !prev)}
      // The open card shows the same states in its body, so the chips are for the closed card.
      badge={
        expanded ? undefined : (
          <SectionHeaderActions>
            {loadError !== null || webApiError !== null ? (
              <SectionErrorChip />
            ) : (
              <>
                {steamChip}
                {webApiChip}
              </>
            )}
          </SectionHeaderActions>
        )
      }
    >
      {loadError !== null ? (
        <ErrorBlock
          title={t('management.sections.integrations.steamCard.loadError')}
          message={loadError}
          retryLabel={t('common.retry')}
          onRetry={() => void refreshSteamAuth()}
        />
      ) : (
        <SteamLoginManager
          authMode={authMode}
          mockMode={mockMode}
          onError={onError}
          onSuccess={onSuccess}
        />
      )}

      <div className="integration-subsection">
        <SteamWebApiStatus />
      </div>
    </AccordionSection>
  );
};

export default SteamIntegrationCard;

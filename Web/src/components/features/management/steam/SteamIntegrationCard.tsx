import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AccordionSection } from '@components/ui/AccordionSection';
import { HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { SectionHeaderActions, SectionHeaderChip } from '@components/ui/SectionHeaderActions';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { SteamIcon } from '@components/ui/SteamIcon';
import { useSteamAuth } from '@contexts/useSteamAuth';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
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
  const { steamAuthMode } = useSteamAuth();
  const activity = useActivityStatus();
  const { status, loading: webApiLoading } = useSteamWebApiStatus();
  const [expanded, setExpanded] = useState(false);
  useAccordionGroupItem('integrations-steam', expanded, () => setExpanded((prev) => !prev));

  const isConnected =
    activity.isActive('integration', 'steam', 'authenticated') || steamAuthMode === 'authenticated';

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
      iconColor="var(--theme-steam)"
      isExpanded={expanded}
      onToggle={() => setExpanded((prev) => !prev)}
      badge={
        <SectionHeaderActions>
          {steamChip}
          {webApiChip}
        </SectionHeaderActions>
      }
    >
      <SteamLoginManager
        authMode={authMode}
        mockMode={mockMode}
        onError={onError}
        onSuccess={onSuccess}
      />

      <div className="integration-subsection">
        <SteamWebApiStatus />
      </div>
    </AccordionSection>
  );
};

export default SteamIntegrationCard;

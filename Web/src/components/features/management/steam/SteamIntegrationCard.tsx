import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AccordionSection } from '@components/ui/AccordionSection';
import { SectionHeaderChip } from '@components/ui/SectionHeaderActions';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { SteamIcon } from '@components/ui/SteamIcon';
import { useSteamAuth } from '@contexts/useSteamAuth';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import { type AuthMode } from '@services/auth.service';
import SteamLoginManager from './SteamLoginManager';
import SteamWebApiStatus from './SteamWebApiStatus';

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
  // Connection state now flows through the unified activity registry; the configured auth mode stays the
  // pre-seed fallback (activity.isActive(...) || existing).
  const activity = useActivityStatus();
  const [expanded, setExpanded] = useState(false);
  useAccordionGroupItem('integrations-steam', expanded, () => setExpanded((prev) => !prev));

  const isConnected =
    activity.isActive('integration', 'steam', 'authenticated') || steamAuthMode === 'authenticated';

  const statusBadge = isConnected ? (
    <SectionHeaderChip variant="success">{t('management.steamAuth.connected')}</SectionHeaderChip>
  ) : (
    <SectionHeaderChip variant="neutral">{t('management.steamAuth.anonymous')}</SectionHeaderChip>
  );

  return (
    <AccordionSection
      title={t('management.sections.integrations.steamCard.title')}
      description={t('management.sections.integrations.steamCard.summary')}
      icon={SteamIcon}
      iconColor="var(--theme-steam)"
      isExpanded={expanded}
      onToggle={() => setExpanded((prev) => !prev)}
      badge={statusBadge}
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

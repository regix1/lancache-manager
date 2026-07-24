import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CheckCircle, User } from 'lucide-react';
import { AccordionSection } from '@components/ui/AccordionSection';
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
    <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-success text-themed-success">
      <CheckCircle size={14} />
      {t('management.steamAuth.connected')}
    </span>
  ) : (
    <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium bg-themed-secondary text-themed-muted">
      <User size={14} />
      {t('management.steamAuth.anonymous')}
    </span>
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

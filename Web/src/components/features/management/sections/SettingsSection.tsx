import React, { useCallback, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Shield, Sparkles, Settings } from 'lucide-react';
import { AccordionSection } from '@components/ui/AccordionSection';
import { AccordionGroupToggle } from '@components/ui/AccordionGroupToggle';
import { GroupHeading } from '@components/ui/GroupHeading';
import { TabPanel } from '@components/features/management/TabPanel';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { SectionHeaderChip } from '@components/ui/SectionHeaderActions';
import { useMockMode } from '@contexts/useMockMode';
import { useAuth } from '@contexts/useAuth';
import { useNotifications } from '@contexts/notifications';
import { useNotifySuccess } from '@/hooks/useErrorHandler';
import AuthenticationManager from '../steam/AuthenticationManager';
import DisplayPreferences from './DisplayPreferences';

const SettingsSection: React.FC = () => {
  const { t } = useTranslation();
  const { mockMode, setMockMode } = useMockMode();
  const { authenticationEnabled } = useAuth();
  const { addNotification } = useNotifications();
  const { notifySuccess } = useNotifySuccess();

  const [apiAuthExpanded, setApiAuthExpanded] = useState(false);
  useAccordionGroupItem('settings-api-auth', apiAuthExpanded, () =>
    setApiAuthExpanded((prev) => !prev)
  );
  const [demoModeExpanded, setDemoModeExpanded] = useState(false);
  useAccordionGroupItem('settings-demo-mode', demoModeExpanded, () =>
    setDemoModeExpanded((prev) => !prev)
  );
  const [displayPrefsExpanded, setDisplayPrefsExpanded] = useState(false);
  useAccordionGroupItem('settings-display-preferences', displayPrefsExpanded, () =>
    setDisplayPrefsExpanded((prev) => !prev)
  );

  const handleError = useCallback(
    (message: string) => {
      addNotification({
        type: 'generic',
        status: 'failed',
        message,
        details: { notificationType: 'error' }
      });
    },
    [addNotification]
  );

  const handleSuccess = useCallback(
    (message: string) => {
      notifySuccess(message);
    },
    [notifySuccess]
  );

  const apiAuthHelpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.sections.settings.help.apiAuthTitle')}>
        {t('management.sections.settings.apiAuthDesc')}
      </HelpSection>
    </HelpPopover>
  );

  const demoModeHelpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.sections.settings.help.demoModeTitle')}>
        {t('management.sections.settings.demoModeDesc')}
      </HelpSection>
    </HelpPopover>
  );

  const displayPreferencesHelpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.sections.settings.help.displayPreferencesTitle')}>
        {t('management.sections.settings.displayPreferencesDesc')}
      </HelpSection>
    </HelpPopover>
  );

  return (
    <TabPanel tabId="settings">
      {/* SYSTEM */}
      <div className="mb-6 sm:mb-8">
        <GroupHeading
          label={t('management.sections.settings.groupSystem')}
          actions={<AccordionGroupToggle />}
        />

        <div className="space-y-4">
          <AccordionSection
            title={t('management.sections.settings.apiAuth')}
            titleAccessory={apiAuthHelpAccessory}
            icon={Shield}
            isExpanded={apiAuthExpanded}
            onToggle={() => setApiAuthExpanded((prev) => !prev)}
            badge={
              <SectionHeaderChip variant={authenticationEnabled ? 'success' : 'neutral'}>
                {authenticationEnabled
                  ? t('management.sections.settings.enabled')
                  : t('management.sections.settings.disabled')}
              </SectionHeaderChip>
            }
          >
            <AuthenticationManager onError={handleError} onSuccess={handleSuccess} />
          </AccordionSection>

          <AccordionSection
            title={t('management.sections.settings.demoMode')}
            titleAccessory={demoModeHelpAccessory}
            icon={Sparkles}
            isExpanded={demoModeExpanded}
            onToggle={() => setDemoModeExpanded((prev) => !prev)}
            badge={
              <SectionHeaderChip variant={mockMode ? 'success' : 'neutral'}>
                {mockMode
                  ? t('management.sections.settings.enabled')
                  : t('management.sections.settings.disabled')}
              </SectionHeaderChip>
            }
          >
            <div className="p-4 rounded-lg bg-themed-tertiary">
              <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
                <div className="flex-1">
                  <p className="text-themed-primary text-sm font-medium">
                    {t('management.sections.settings.mockData')}
                  </p>
                </div>
                <Button
                  onClick={() => setMockMode(!mockMode)}
                  variant="filled"
                  color={mockMode ? 'primary' : 'secondary'}
                  className="w-full sm:w-36"
                >
                  {mockMode
                    ? t('management.sections.settings.enabled')
                    : t('management.sections.settings.disabled')}
                </Button>
              </div>
            </div>
            {mockMode && (
              <div className="mt-4">
                <Alert color="blue">
                  <span className="text-sm">
                    {t('management.sections.settings.mockModeActive')}
                  </span>
                </Alert>
              </div>
            )}
          </AccordionSection>
        </div>
      </div>

      {/* PREFERENCES */}
      <div>
        <GroupHeading label={t('management.sections.settings.groupPreferences')} />

        <div className="space-y-4">
          <AccordionSection
            title={t('management.sections.settings.displayPreferences')}
            titleAccessory={displayPreferencesHelpAccessory}
            icon={Settings}
            isExpanded={displayPrefsExpanded}
            onToggle={() => setDisplayPrefsExpanded((prev) => !prev)}
          >
            <DisplayPreferences />
          </AccordionSection>
        </div>
      </div>
    </TabPanel>
  );
};

export default SettingsSection;

import React from 'react';
import { useTranslation } from 'react-i18next';
import {
  Plug,
  HardDrive,
  FolderCog,
  Settings,
  Users,
  Palette,
  Container,
  Calendar,
  MonitorCheck,
  type LucideIcon
} from 'lucide-react';

export type ManagementSection =
  | 'settings'
  | 'integrations'
  | 'storage'
  | 'data'
  | 'schedules'
  | 'preferences'
  | 'clients'
  | 'prefill-sessions'
  | 'status-check';

interface TabConfig {
  id: ManagementSection;
  labelKey: string;
  shortLabelKey: string;
  icon: LucideIcon;
  descriptionKey: string;
}

interface ManagementNavProps {
  activeSection: ManagementSection;
  onSectionChange: (section: ManagementSection) => void;
  isAdmin: boolean;
}

const ManagementNav: React.FC<ManagementNavProps> = ({
  activeSection,
  onSectionChange,
  isAdmin
}) => {
  const { t } = useTranslation();

  const tabs: TabConfig[] = [
    {
      id: 'settings',
      labelKey: 'management.nav.settings',
      shortLabelKey: 'management.nav.settingsShort',
      icon: Settings,
      descriptionKey: 'management.nav.settingsDesc'
    },
    {
      id: 'integrations',
      labelKey: 'management.nav.integrations',
      shortLabelKey: 'management.nav.integrationsShort',
      icon: Plug,
      descriptionKey: 'management.nav.integrationsDesc'
    },
    {
      id: 'storage',
      labelKey: 'management.nav.storage',
      shortLabelKey: 'management.nav.storageShort',
      icon: HardDrive,
      descriptionKey: 'management.nav.storageDesc'
    },
    {
      id: 'data',
      labelKey: 'management.nav.data',
      shortLabelKey: 'management.nav.dataShort',
      icon: FolderCog,
      descriptionKey: 'management.nav.dataDesc'
    },
    {
      id: 'schedules',
      labelKey: 'management.nav.schedules',
      shortLabelKey: 'management.nav.schedulesShort',
      icon: Calendar,
      descriptionKey: 'management.nav.schedulesDesc'
    },
    {
      id: 'preferences',
      labelKey: 'management.nav.preferences',
      shortLabelKey: 'management.nav.preferencesShort',
      icon: Palette,
      descriptionKey: 'management.nav.preferencesDesc'
    },
    {
      id: 'clients',
      labelKey: 'management.nav.clients',
      shortLabelKey: 'management.nav.clientsShort',
      icon: Users,
      descriptionKey: 'management.nav.clientsDesc'
    },
    {
      id: 'prefill-sessions',
      labelKey: 'management.nav.prefillSessions',
      shortLabelKey: 'management.nav.prefillSessionsShort',
      icon: Container,
      descriptionKey: 'management.nav.prefillSessionsDesc'
    },
    {
      id: 'status-check',
      labelKey: 'management.nav.statusCheck',
      shortLabelKey: 'management.nav.statusCheckShort',
      icon: MonitorCheck,
      descriptionKey: 'management.nav.statusCheckDesc'
    }
  ];

  return (
    <div className="management-nav-container mb-6">
      {/* Desktop Navigation */}
      <nav className="hidden md:block" role="tablist" aria-label={t('aria.managementSections')}>
        {/* overflow-x-auto + non-shrinking tabs: the row fills the full width when
            there is room, and becomes horizontally scrollable instead of clipping
            the last tabs at in-between viewport widths (same pattern as the
            mobile row below). */}
        <div className="rounded-lg border bg-themed-secondary border-themed-primary overflow-x-auto custom-scrollbar">
          <div className="flex">
            {tabs.map((tab, index) => {
              const isActive = activeSection === tab.id;
              const isDisabled = tab.id !== 'settings' && !isAdmin;
              const Icon = tab.icon;
              const isFirst = index === 0;
              const isLast = index === tabs.length - 1;

              return (
                <button
                  key={tab.id}
                  role="tab"
                  aria-selected={isActive}
                  aria-controls={`panel-${tab.id}`}
                  disabled={isDisabled}
                  onClick={() => !isDisabled && onSectionChange(tab.id)}
                  className={`
                    management-nav-tab
                    relative flex-grow flex-shrink-0 flex items-center justify-center gap-2
                    px-5 py-3.5 font-medium text-sm
                    border-r last:border-r-0
                    ${isFirst ? 'rounded-l-lg' : ''}
                    ${isLast ? 'rounded-r-lg' : ''}
                    ${isDisabled ? 'opacity-40 cursor-not-allowed' : 'cursor-pointer'}
                  `}
                >
                  <Icon className="w-4 h-4 flex-shrink-0 nav-tab-icon" />
                  <span className="whitespace-nowrap">{t(tab.labelKey)}</span>

                  {/* Active indicator bar */}
                  {isActive && (
                    <div className="nav-indicator absolute bottom-0 left-2 right-2 h-0.5 rounded bg-themed-nav-tab-active-border" />
                  )}
                </button>
              );
            })}
          </div>
        </div>
      </nav>

      {/* Mobile Navigation */}
      <nav className="md:hidden" role="tablist" aria-label={t('aria.managementSections')}>
        {/* Active section header */}
        <div className="flex items-center gap-3 px-4 py-3 rounded-t-lg border border-b-0 bg-themed-secondary border-themed-primary">
          {(() => {
            const activeTab = tabs.find((tabItem) => tabItem.id === activeSection);
            const Icon = activeTab?.icon || Settings;
            return (
              <>
                <Icon className="w-5 h-5 text-themed-nav-tab-active" />
                <div className="flex-1">
                  <div className="font-semibold text-themed-primary">
                    {activeTab ? t(activeTab.labelKey) : ''}
                  </div>
                  <div className="text-xs text-themed-muted">
                    {activeTab ? t(activeTab.descriptionKey) : ''}
                  </div>
                </div>
              </>
            );
          })()}
        </div>

        {/* Tab buttons row */}
        <div className="flex overflow-x-auto gap-0.5 p-1 rounded-b-lg border border-t-0 custom-scrollbar bg-themed-tertiary border-themed-primary">
          {tabs.map((tab) => {
            const isActive = activeSection === tab.id;
            const isDisabled = tab.id !== 'settings' && !isAdmin;
            const Icon = tab.icon;

            return (
              <button
                key={tab.id}
                role="tab"
                aria-selected={isActive}
                aria-controls={`panel-${tab.id}`}
                disabled={isDisabled}
                onClick={() => !isDisabled && onSectionChange(tab.id)}
                className={`
                  management-nav-tab management-nav-tab--mobile
                  flex-shrink-0 flex items-center justify-center gap-1.5
                  px-3 text-xs font-medium rounded-md
                  ${isDisabled ? 'opacity-40 cursor-not-allowed' : 'cursor-pointer'}
                `}
              >
                <Icon className="w-3.5 h-3.5 nav-tab-icon" />
                <span className="whitespace-nowrap">{t(tab.shortLabelKey)}</span>
              </button>
            );
          })}
        </div>
      </nav>
    </div>
  );
};

export default ManagementNav;

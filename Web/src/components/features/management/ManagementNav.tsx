import React from 'react';
import {
  Plug,
  HardDrive,
  FolderCog,
  Settings,
  Users,
  Palette,
  Container,
  type LucideIcon
} from 'lucide-react';

export type ManagementSection =
  | 'settings'
  | 'integrations'
  | 'storage'
  | 'data'
  | 'preferences'
  | 'clients'
  | 'prefill-sessions';

interface TabConfig {
  id: ManagementSection;
  label: string;
  shortLabel: string;
  icon: LucideIcon;
  description: string;
}

const tabs: TabConfig[] = [
  {
    id: 'settings',
    label: 'Settings',
    shortLabel: 'Settings',
    icon: Settings,
    description: 'Display & behavior preferences'
  },
  {
    id: 'integrations',
    label: 'Integrations',
    shortLabel: 'Integrate',
    icon: Plug,
    description: 'Steam, Grafana & external services'
  },
  {
    id: 'storage',
    label: 'Logs & Cache',
    shortLabel: 'Logs & Cache',
    icon: HardDrive,
    description: 'Cache, logs & game detection'
  },
  {
    id: 'data',
    label: 'Data',
    shortLabel: 'Data',
    icon: FolderCog,
    description: 'Depot mappings & database'
  },
  {
    id: 'preferences',
    label: 'Theme',
    shortLabel: 'Theme',
    icon: Palette,
    description: 'Themes & customization'
  },
  {
    id: 'clients',
    label: 'Clients',
    shortLabel: 'Clients',
    icon: Users,
    description: 'Assign nicknames to client IPs'
  },
  {
    id: 'prefill-sessions',
    label: 'Prefill Sessions',
    shortLabel: 'Prefill',
    icon: Container,
    description: 'Manage Steam Prefill daemon sessions'
  }
];

interface ManagementNavProps {
  activeSection: ManagementSection;
  onSectionChange: (section: ManagementSection) => void;
  isAuthenticated: boolean;
}

const ManagementNav: React.FC<ManagementNavProps> = ({
  activeSection,
  onSectionChange,
  isAuthenticated
}) => {
  return (
    <div className="management-nav-container mb-6">
      {/* Desktop Navigation */}
      <nav
        className="hidden md:block"
        role="tablist"
        aria-label="Management sections"
      >
        <div
          className="rounded-lg border"
          style={{
            backgroundColor: 'var(--theme-bg-secondary)',
            borderColor: 'var(--theme-border-primary)'
          }}
        >
          <div className="flex">
            {tabs.map((tab, index) => {
              const isActive = activeSection === tab.id;
              const isDisabled = tab.id !== 'settings' && !isAuthenticated;
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
                    relative flex-1 flex items-center justify-center gap-2
                    px-5 py-3.5 font-medium text-sm
                    transition-all duration-200 ease-out
                    border-r last:border-r-0
                    ${isFirst ? 'rounded-l-lg' : ''}
                    ${isLast ? 'rounded-r-lg' : ''}
                    ${isDisabled ? 'opacity-40 cursor-not-allowed' : 'cursor-pointer'}
                  `}
                  style={{
                    backgroundColor: isActive ? 'var(--theme-bg-primary)' : 'transparent',
                    color: isActive
                      ? 'var(--theme-nav-tab-active)'
                      : 'var(--theme-nav-tab-inactive)',
                    borderColor: 'var(--theme-border-primary)'
                  }}
                  onMouseEnter={(e) => {
                    if (!isActive && !isDisabled) {
                      e.currentTarget.style.color = 'var(--theme-nav-tab-hover)';
                      e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                    }
                  }}
                  onMouseLeave={(e) => {
                    if (!isActive && !isDisabled) {
                      e.currentTarget.style.color = 'var(--theme-nav-tab-inactive)';
                      e.currentTarget.style.backgroundColor = 'transparent';
                    }
                  }}
                >
                  <Icon
                    className="w-4 h-4 flex-shrink-0"
                    style={{
                      color: isActive
                        ? 'var(--theme-nav-tab-active)'
                        : 'inherit'
                    }}
                  />
                  <span className="whitespace-nowrap">{tab.label}</span>

                  {/* Active indicator bar */}
                  {isActive && (
                    <div
                      className="absolute bottom-0 left-2 right-2 h-0.5 rounded-full"
                      style={{ backgroundColor: 'var(--theme-nav-tab-active-border)' }}
                    />
                  )}
                </button>
              );
            })}
          </div>
        </div>
      </nav>

      {/* Mobile Navigation */}
      <nav
        className="md:hidden"
        role="tablist"
        aria-label="Management sections"
      >
        {/* Active section header */}
        <div
          className="flex items-center gap-3 px-4 py-3 rounded-t-lg border border-b-0"
          style={{
            backgroundColor: 'var(--theme-bg-secondary)',
            borderColor: 'var(--theme-border-primary)'
          }}
        >
          {(() => {
            const activeTab = tabs.find(t => t.id === activeSection);
            const Icon = activeTab?.icon || Settings;
            return (
              <>
                <Icon
                  className="w-5 h-5"
                  style={{ color: 'var(--theme-nav-tab-active)' }}
                />
                <div className="flex-1">
                  <div
                    className="font-semibold"
                    style={{ color: 'var(--theme-text-primary)' }}
                  >
                    {activeTab?.label}
                  </div>
                  <div
                    className="text-xs"
                    style={{ color: 'var(--theme-text-muted)' }}
                  >
                    {activeTab?.description}
                  </div>
                </div>
              </>
            );
          })()}
        </div>

        {/* Tab buttons row */}
        <div
          className="flex overflow-x-auto gap-0.5 p-1 rounded-b-lg border border-t-0 custom-scrollbar"
          style={{
            backgroundColor: 'var(--theme-bg-tertiary)',
            borderColor: 'var(--theme-border-primary)'
          }}
        >
          {tabs.map((tab) => {
            const isActive = activeSection === tab.id;
            const isDisabled = tab.id !== 'settings' && !isAuthenticated;
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
                  flex-shrink-0 flex items-center justify-center gap-1.5
                  px-3 py-2 rounded-md text-xs font-medium
                  transition-all duration-200
                  ${isDisabled ? 'opacity-40 cursor-not-allowed' : 'cursor-pointer'}
                `}
                style={{
                  backgroundColor: isActive ? 'var(--theme-primary)' : 'transparent',
                  color: isActive
                    ? 'var(--theme-button-text)'
                    : 'var(--theme-nav-tab-inactive)'
                }}
                onMouseEnter={(e) => {
                  if (!isActive && !isDisabled) {
                    e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
                    e.currentTarget.style.color = 'var(--theme-nav-tab-hover)';
                  }
                }}
                onMouseLeave={(e) => {
                  if (!isActive && !isDisabled) {
                    e.currentTarget.style.backgroundColor = 'transparent';
                    e.currentTarget.style.color = 'var(--theme-nav-tab-inactive)';
                  }
                }}
              >
                <Icon className="w-3.5 h-3.5" />
                <span className="whitespace-nowrap">{tab.shortLabel}</span>
              </button>
            );
          })}
        </div>
      </nav>
    </div>
  );
};

export default ManagementNav;

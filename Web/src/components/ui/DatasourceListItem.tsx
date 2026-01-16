import React from 'react';
import { FolderOpen, ChevronDown } from 'lucide-react';
import { useTranslation } from 'react-i18next';

interface DatasourceListItemProps {
  name: string;
  path: string;
  isExpanded: boolean;
  onToggle: () => void;
  enabled?: boolean;
  statusBadge?: React.ReactNode;
  statusIcons?: React.ReactNode;
  children?: React.ReactNode;
}

export const DatasourceListItem: React.FC<DatasourceListItemProps> = ({
  name,
  path,
  isExpanded,
  onToggle,
  enabled = true,
  statusBadge,
  statusIcons,
  children
}) => {
  const { t } = useTranslation();
  
  return (
    <div
      className={`group rounded-lg overflow-hidden transition-all duration-300 border ${
        isExpanded
          ? `bg-themed-secondary ${enabled ? 'border-themed-primary' : 'border-themed-secondary'} shadow-[0_4px_16px_rgba(0,0,0,0.25),0_1px_4px_rgba(0,0,0,0.15)]`
          : 'bg-[color-mix(in_srgb,var(--theme-bg-secondary)_60%,transparent)] border-themed-secondary shadow-[0_1px_3px_rgba(0,0,0,0.12)]'
      } ${enabled ? 'opacity-100' : 'opacity-65'}`}
    >
      {/* Header - clickable to expand */}
      <button
        className={`w-full p-3 text-left cursor-pointer transition-all duration-200 ${
          isExpanded
            ? 'bg-[linear-gradient(135deg,color-mix(in_srgb,var(--theme-accent)_6%,transparent)_0%,transparent_100%)]'
            : 'bg-transparent'
        }`}
        onClick={onToggle}
      >
        {/* Top Row: Name, Status Badge, Chevron */}
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2.5">
            {/* Folder Icon */}
            <div
              className={`w-7 h-7 rounded-md flex items-center justify-center transition-all duration-300 ${
                isExpanded
                  ? 'bg-[color-mix(in_srgb,var(--theme-icon-blue)_15%,transparent)] scale-105'
                  : 'bg-themed-tertiary scale-100'
              }`}
            >
              <FolderOpen
                className={`w-3.5 h-3.5 transition-colors duration-300 ${
                  isExpanded ? 'icon-blue' : 'text-themed-muted'
                }`}
              />
            </div>

            {/* Name */}
            <span
              className={`font-semibold transition-colors duration-200 ${
                isExpanded ? 'text-themed-primary' : 'text-themed-secondary'
              }`}
            >
              {name}
            </span>

            {/* Disabled Badge */}
            {!enabled && (
              <span className="px-2 py-0.5 text-xs rounded font-medium bg-themed-tertiary text-themed-muted">
                {t('ui.datasource.disabled')}
              </span>
            )}
          </div>

          <div className="flex items-center gap-3">
            {/* Status Badge */}
            {statusBadge && (
              <span
                className={`text-xs hidden sm:inline px-2.5 py-1 rounded-full transition-all duration-300 tabular-nums ${
                  isExpanded
                    ? 'bg-[color-mix(in_srgb,var(--theme-accent)_12%,transparent)] text-themed-accent'
                    : 'bg-themed-tertiary text-themed-muted'
                }`}
              >
                {statusBadge}
              </span>
            )}

            {statusIcons}

            {/* Chevron with rotation animation */}
            <div
              className={`flex items-center justify-center w-7 h-7 rounded-md transition-all duration-300 ${
                isExpanded
                  ? 'bg-[color-mix(in_srgb,var(--theme-accent)_10%,transparent)]'
                  : 'bg-transparent'
              }`}
            >
              <ChevronDown
                className={`w-4 h-4 transition-all duration-300 ease-out ${
                  isExpanded ? 'rotate-180 text-themed-accent' : 'rotate-0 text-themed-muted'
                }`}
              />
            </div>
          </div>
        </div>

        {/* Path display */}
        <div
          className={`flex items-center gap-2 mt-2 transition-all duration-300 ${
            isExpanded ? 'opacity-100' : 'opacity-70'
          }`}
        >
          <code
            className={`text-xs px-2 py-1 rounded truncate transition-all duration-300 max-w-full ${
              isExpanded
                ? 'bg-[color-mix(in_srgb,var(--theme-bg-tertiary)_80%,transparent)] text-themed-secondary'
                : 'bg-themed-tertiary text-themed-muted'
            }`}
          >
            {path}
          </code>
        </div>
      </button>

      {/* Expanded content with smooth animation */}
      {children && (
        <div
          className={`overflow-hidden transition-all duration-300 ease-out ${
            isExpanded
              ? 'max-h-[2000px] opacity-100 translate-y-0'
              : 'max-h-0 opacity-0 -translate-y-2'
          }`}
        >
          <div
            className="px-3 pb-3 border-t border-themed-secondary bg-[linear-gradient(180deg,color-mix(in_srgb,var(--theme-bg-tertiary)_25%,transparent)_0%,transparent_100%)]"
          >
            {children}
          </div>
        </div>
      )}
    </div>
  );
};

export default DatasourceListItem;

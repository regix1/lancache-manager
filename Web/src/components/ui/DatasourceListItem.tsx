import React from 'react';
import { FolderOpen, ChevronDown } from 'lucide-react';

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
  return (
    <div
      className="group rounded-lg overflow-hidden transition-all duration-300"
      style={{
        backgroundColor: isExpanded
          ? 'var(--theme-bg-secondary)'
          : 'color-mix(in srgb, var(--theme-bg-secondary) 60%, transparent)',
        border: '1px solid',
        borderColor: isExpanded
          ? enabled
            ? 'var(--theme-border-primary)'
            : 'var(--theme-border-secondary)'
          : 'var(--theme-border-secondary)',
        opacity: enabled ? 1 : 0.65,
        boxShadow: isExpanded
          ? '0 4px 16px color-mix(in srgb, var(--theme-text-primary) 12%, transparent), 0 1px 4px color-mix(in srgb, var(--theme-text-primary) 8%, transparent)'
          : '0 1px 3px color-mix(in srgb, var(--theme-text-primary) 6%, transparent)'
      }}
    >
      {/* Header - clickable to expand */}
      <button
        className="w-full p-3 text-left cursor-pointer transition-all duration-200"
        onClick={onToggle}
        style={{
          background: isExpanded
            ? 'linear-gradient(135deg, color-mix(in srgb, var(--theme-accent) 6%, transparent) 0%, transparent 100%)'
            : 'transparent'
        }}
      >
        {/* Top Row: Name, Status Badge, Chevron */}
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2.5">
            {/* Folder Icon */}
            <div
              className="w-7 h-7 rounded-md flex items-center justify-center transition-all duration-300"
              style={{
                backgroundColor: isExpanded
                  ? 'color-mix(in srgb, var(--theme-icon-blue) 15%, transparent)'
                  : 'var(--theme-bg-tertiary)',
                transform: isExpanded ? 'scale(1.05)' : 'scale(1)'
              }}
            >
              <FolderOpen
                className="w-3.5 h-3.5 transition-colors duration-300"
                style={{
                  color: isExpanded ? 'var(--theme-icon-blue)' : 'var(--theme-text-muted)'
                }}
              />
            </div>

            {/* Name */}
            <span
              className="font-semibold transition-colors duration-200"
              style={{
                color: isExpanded ? 'var(--theme-text-primary)' : 'var(--theme-text-secondary)'
              }}
            >
              {name}
            </span>

            {/* Disabled Badge */}
            {!enabled && (
              <span
                className="px-2 py-0.5 text-xs rounded font-medium"
                style={{
                  backgroundColor: 'var(--theme-bg-tertiary)',
                  color: 'var(--theme-text-muted)'
                }}
              >
                Disabled
              </span>
            )}
          </div>

          <div className="flex items-center gap-3">
            {/* Status Badge */}
            {statusBadge && (
              <span
                className="text-xs hidden sm:inline px-2.5 py-1 rounded-full transition-all duration-300 tabular-nums"
                style={{
                  backgroundColor: isExpanded
                    ? 'color-mix(in srgb, var(--theme-accent) 12%, transparent)'
                    : 'var(--theme-bg-tertiary)',
                  color: isExpanded ? 'var(--theme-accent)' : 'var(--theme-text-muted)'
                }}
              >
                {statusBadge}
              </span>
            )}

            {statusIcons}

            {/* Chevron with rotation animation */}
            <div
              className="flex items-center justify-center w-7 h-7 rounded-md transition-all duration-300"
              style={{
                backgroundColor: isExpanded
                  ? 'color-mix(in srgb, var(--theme-accent) 10%, transparent)'
                  : 'transparent'
              }}
            >
              <ChevronDown
                className="w-4 h-4 transition-all duration-300 ease-out"
                style={{
                  color: isExpanded ? 'var(--theme-accent)' : 'var(--theme-text-muted)',
                  transform: isExpanded ? 'rotate(180deg)' : 'rotate(0deg)'
                }}
              />
            </div>
          </div>
        </div>

        {/* Path display */}
        <div
          className="flex items-center gap-2 mt-2 transition-all duration-300"
          style={{
            opacity: isExpanded ? 1 : 0.7,
            transform: isExpanded ? 'translateX(0)' : 'translateX(0)'
          }}
        >
          <code
            className="text-xs px-2 py-1 rounded truncate transition-all duration-300"
            style={{
              backgroundColor: isExpanded
                ? 'color-mix(in srgb, var(--theme-bg-tertiary) 80%, transparent)'
                : 'var(--theme-bg-tertiary)',
              color: isExpanded ? 'var(--theme-text-secondary)' : 'var(--theme-text-muted)',
              maxWidth: '100%'
            }}
          >
            {path}
          </code>
        </div>
      </button>

      {/* Expanded content with smooth animation */}
      {children && (
        <div
          className="overflow-hidden transition-all duration-300 ease-out"
          style={{
            maxHeight: isExpanded ? '2000px' : '0',
            opacity: isExpanded ? 1 : 0,
            transform: isExpanded ? 'translateY(0)' : 'translateY(-8px)'
          }}
        >
          <div
            className="px-3 pb-3"
            style={{
              borderTop: '1px solid var(--theme-border-secondary)',
              background: 'linear-gradient(180deg, color-mix(in srgb, var(--theme-bg-tertiary) 25%, transparent) 0%, transparent 100%)'
            }}
          >
            {children}
          </div>
        </div>
      )}
    </div>
  );
};

export default DatasourceListItem;

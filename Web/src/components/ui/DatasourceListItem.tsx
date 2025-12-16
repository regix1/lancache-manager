import React from 'react';
import { FolderOpen, ChevronDown, ChevronUp } from 'lucide-react';

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
      className="rounded-lg border"
      style={{
        backgroundColor: 'var(--theme-bg-secondary)',
        borderColor: enabled ? 'var(--theme-border-primary)' : 'var(--theme-border-secondary)',
        opacity: enabled ? 1 : 0.6
      }}
    >
      {/* Header - clickable to expand */}
      <div
        className="p-3 cursor-pointer"
        onClick={onToggle}
      >
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <span className="font-semibold text-themed-primary">{name}</span>
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
            {statusBadge && (
              <span className="text-xs text-themed-muted hidden sm:inline">
                {statusBadge}
              </span>
            )}
            {statusIcons}
            {isExpanded ? (
              <ChevronUp className="w-4 h-4 text-themed-muted" />
            ) : (
              <ChevronDown className="w-4 h-4 text-themed-muted" />
            )}
          </div>
        </div>
        {/* Path display */}
        <div className="flex items-center gap-2 text-xs text-themed-muted mt-1">
          <FolderOpen className="w-3.5 h-3.5 flex-shrink-0" />
          <code className="bg-themed-tertiary px-1.5 py-0.5 rounded text-themed-secondary truncate">
            {path}
          </code>
        </div>
      </div>

      {/* Expanded content */}
      {isExpanded && children && (
        <div className="px-3 pb-3 border-t" style={{ borderColor: 'var(--theme-border-secondary)' }}>
          {children}
        </div>
      )}
    </div>
  );
};

export default DatasourceListItem;

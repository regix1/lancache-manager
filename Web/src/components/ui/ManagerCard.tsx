import React from 'react';
import {
  Loader2,
  RefreshCw,
  ScrollText,
  HardDrive,
  CheckCircle,
  XCircle,
  Lock
} from 'lucide-react';
import { Button } from './Button';
import { Tooltip } from './Tooltip';

// ============================================================================
// TYPES
// ============================================================================

export type IconColor = 'red' | 'orange' | 'yellow' | 'green' | 'blue' | 'purple' | 'cyan' | 'indigo';

// Safelist for CSS purging - these classes are used dynamically via `icon-bg-${iconColor}`
// prettier-ignore
const _iconClassSafelist = [
  'icon-bg-red', 'icon-bg-orange', 'icon-bg-yellow', 'icon-bg-green',
  'icon-bg-blue', 'icon-bg-purple', 'icon-bg-cyan', 'icon-bg-indigo',
  'icon-red', 'icon-orange', 'icon-yellow', 'icon-green',
  'icon-blue', 'icon-purple', 'icon-cyan', 'icon-indigo',
];
void _iconClassSafelist;

export interface PermissionStatus {
  logsReadOnly?: boolean;
  cacheReadOnly?: boolean;
  dockerSocketAvailable?: boolean;
  checkingPermissions?: boolean;
}

// ============================================================================
// MANAGER CARD HEADER
// ============================================================================

interface ManagerCardHeaderProps {
  icon: React.ElementType;
  iconColor: IconColor;
  title: string;
  subtitle: string;
  helpContent?: React.ReactNode;
  permissions?: PermissionStatus;
  actions?: React.ReactNode;
}

/**
 * Standardized header for all management cards.
 * Includes: Icon, Title, Subtitle, Help popover, Permission indicators, Action buttons
 */
export const ManagerCardHeader: React.FC<ManagerCardHeaderProps> = ({
  icon: Icon,
  iconColor,
  title,
  subtitle,
  helpContent,
  permissions,
  actions
}) => {
  return (
    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 sm:gap-4 mb-6">
      <div className="flex items-center gap-3 min-w-0">
        <div className={`w-10 h-10 rounded-lg flex items-center justify-center icon-bg-${iconColor} flex-shrink-0`}>
          <Icon className={`w-5 h-5 icon-${iconColor}`} />
        </div>
        <div className="min-w-0 flex-1">
          <h3 className="text-lg font-semibold text-themed-primary truncate">{title}</h3>
          <p className="text-xs text-themed-muted truncate">{subtitle}</p>
        </div>
        {helpContent}
      </div>
      <div className="flex items-center gap-2 sm:gap-3 flex-wrap justify-end sm:justify-start flex-shrink-0">
        {permissions && !permissions.checkingPermissions && (
          <PermissionIndicators {...permissions} />
        )}
        {actions}
      </div>
    </div>
  );
};

// ============================================================================
// PERMISSION INDICATORS
// ============================================================================

interface PermissionIndicatorsProps {
  logsReadOnly?: boolean;
  cacheReadOnly?: boolean;
  showLogs?: boolean;
  showCache?: boolean;
}

/**
 * Standardized permission status indicators (logs/cache writable status)
 */
export const PermissionIndicators: React.FC<PermissionIndicatorsProps> = ({
  logsReadOnly,
  cacheReadOnly,
  showLogs = true,
  showCache = true
}) => {
  return (
    <div className="flex items-center gap-2">
      {showLogs && logsReadOnly !== undefined && (
        <Tooltip
          content={logsReadOnly ? 'Logs are read-only' : 'Logs are writable'}
          position="top"
        >
          <span className="flex items-center gap-0.5">
            <ScrollText className="w-3.5 h-3.5 text-themed-muted" />
            {logsReadOnly ? (
              <XCircle className="w-4 h-4" style={{ color: 'var(--theme-warning)' }} />
            ) : (
              <CheckCircle className="w-4 h-4" style={{ color: 'var(--theme-success-text)' }} />
            )}
          </span>
        </Tooltip>
      )}
      {showCache && cacheReadOnly !== undefined && (
        <Tooltip
          content={cacheReadOnly ? 'Cache is read-only' : 'Cache is writable'}
          position="top"
        >
          <span className="flex items-center gap-0.5">
            <HardDrive className="w-3.5 h-3.5 text-themed-muted" />
            {cacheReadOnly ? (
              <XCircle className="w-4 h-4" style={{ color: 'var(--theme-warning)' }} />
            ) : (
              <CheckCircle className="w-4 h-4" style={{ color: 'var(--theme-success-text)' }} />
            )}
          </span>
        </Tooltip>
      )}
    </div>
  );
};

// ============================================================================
// ACTION BUTTONS
// ============================================================================

interface RefreshButtonProps {
  onClick: () => void;
  isLoading?: boolean;
  disabled?: boolean;
  tooltip?: string;
}

/**
 * Standardized refresh button for management cards
 */
export const RefreshButton: React.FC<RefreshButtonProps> = ({
  onClick,
  isLoading = false,
  disabled = false,
  tooltip = 'Refresh data'
}) => {
  return (
    <Tooltip content={tooltip} position="top">
      <Button
        onClick={onClick}
        disabled={disabled || isLoading}
        variant="subtle"
        size="sm"
      >
        {isLoading ? (
          <Loader2 className="w-4 h-4 animate-spin" />
        ) : (
          <RefreshCw className="w-4 h-4" />
        )}
      </Button>
    </Tooltip>
  );
};

// ============================================================================
// LOADING STATE
// ============================================================================

interface LoadingStateProps {
  message?: string;
  submessage?: string;
}

/**
 * Standardized loading state for management cards
 */
export const LoadingState: React.FC<LoadingStateProps> = ({
  message = 'Loading...',
  submessage
}) => {
  return (
    <div className="flex flex-col items-center justify-center py-8 gap-3">
      <Loader2 className="w-6 h-6 animate-spin text-themed-accent" />
      <p className="text-sm text-themed-secondary">{message}</p>
      {submessage && (
        <p className="text-xs text-themed-muted">{submessage}</p>
      )}
    </div>
  );
};

// ============================================================================
// EMPTY STATE
// ============================================================================

interface EmptyStateProps {
  icon?: React.ElementType;
  title: string;
  subtitle?: string;
  action?: React.ReactNode;
}

/**
 * Standardized empty state for management cards
 */
export const EmptyState: React.FC<EmptyStateProps> = ({
  icon: Icon,
  title,
  subtitle,
  action
}) => {
  return (
    <div className="text-center py-8 text-themed-muted">
      {Icon && <Icon className="w-12 h-12 mx-auto mb-3 opacity-50" />}
      <div className="mb-2">{title}</div>
      {subtitle && <div className="text-xs">{subtitle}</div>}
      {action && <div className="mt-4">{action}</div>}
    </div>
  );
};

// ============================================================================
// READ ONLY BADGE
// ============================================================================

interface ReadOnlyBadgeProps {
  message?: string;
}

/**
 * Standardized read-only badge for disabled states
 */
export const ReadOnlyBadge: React.FC<ReadOnlyBadgeProps> = ({
  message = 'Read-only'
}) => {
  return (
    <div className="flex items-center justify-center py-4">
      <span
        className="px-2 py-0.5 text-xs rounded font-medium flex items-center gap-1.5 border"
        style={{
          backgroundColor: 'var(--theme-warning-bg)',
          color: 'var(--theme-warning)',
          borderColor: 'var(--theme-warning)'
        }}
      >
        <Lock className="w-3 h-3" />
        {message}
      </span>
    </div>
  );
};

// ============================================================================
// SCANNING STATE (for background operations)
// ============================================================================

interface ScanningStateProps {
  message: string;
}

/**
 * Standardized scanning/processing state alert
 */
export const ScanningState: React.FC<ScanningStateProps> = ({ message }) => {
  return (
    <div
      className="flex items-center gap-2 p-3 rounded-lg mb-4"
      style={{
        backgroundColor: 'var(--theme-info-bg)',
        border: '1px solid var(--theme-info)'
      }}
    >
      <Loader2 className="w-4 h-4 animate-spin" style={{ color: 'var(--theme-info)' }} />
      <span className="text-sm" style={{ color: 'var(--theme-info-text)' }}>
        {message}
      </span>
    </div>
  );
};

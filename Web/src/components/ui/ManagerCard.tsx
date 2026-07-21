import React from 'react';
import { Lock } from 'lucide-react';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { useTranslation } from 'react-i18next';

// ============================================================================
// TYPES
// ============================================================================

type IconColor = 'red' | 'orange' | 'yellow' | 'green' | 'blue' | 'purple' | 'cyan' | 'indigo';

// Safelist for CSS purging - these classes are used dynamically via `icon-bg-${iconColor}`
// prettier-ignore
const _iconClassSafelist = [
  'icon-bg-red', 'icon-bg-orange', 'icon-bg-yellow', 'icon-bg-green',
  'icon-bg-blue', 'icon-bg-purple', 'icon-bg-cyan', 'icon-bg-indigo',
  'icon-red', 'icon-orange', 'icon-yellow', 'icon-green',
  'icon-blue', 'icon-purple', 'icon-cyan', 'icon-indigo',
];
void _iconClassSafelist;

// ============================================================================
// MANAGER CARD HEADER
// ============================================================================

interface ManagerCardHeaderProps {
  icon: React.ElementType;
  iconColor: IconColor;
  title: string;
  subtitle: string;
  helpContent?: React.ReactNode;
  actions?: React.ReactNode;
}

/**
 * Standardized header for all management cards.
 * Includes: Icon, Title, Subtitle, Help popover, Action buttons
 */
export const ManagerCardHeader: React.FC<ManagerCardHeaderProps> = ({
  icon: Icon,
  iconColor,
  title,
  subtitle,
  helpContent,
  actions
}) => {
  return (
    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 sm:gap-4 mb-6">
      <div className="flex items-center gap-3 min-w-0">
        <div className={`icon-box icon-box--md icon-bg-${iconColor}`}>
          <Icon className={`w-5 h-5 icon-${iconColor}`} />
        </div>
        <div className="min-w-0 flex-1">
          <h3 className="text-lg font-semibold text-themed-primary truncate">{title}</h3>
          <p className="text-xs text-themed-muted truncate">{subtitle}</p>
        </div>
        {helpContent}
      </div>
      <div className="flex items-center gap-2 sm:gap-3 flex-wrap justify-end sm:justify-start flex-shrink-0">
        {actions}
      </div>
    </div>
  );
};

// ============================================================================
// LOADING STATE
// ============================================================================

interface LoadingStateProps {
  message?: string;
  submessage?: string;
  /**
   * 'skeleton' (default) renders placeholder rows that mirror a content list, so
   * a panel waiting on data reads as its final shape instead of a spinner void.
   * 'spinner' keeps the centered spinner for spot loads with no list shape.
   */
  variant?: 'skeleton' | 'spinner';
  /** Skeleton row count (skeleton variant only). */
  rows?: number;
}

/**
 * Standardized loading state for management cards. Defaults to a skeleton list;
 * pass variant="spinner" for a small centered spinner instead.
 */
export const LoadingState: React.FC<LoadingStateProps> = ({
  message,
  submessage,
  variant = 'skeleton',
  rows = 4
}) => {
  const { t } = useTranslation();

  if (variant === 'spinner') {
    return (
      <div className="flex flex-col items-center justify-center py-8 gap-3">
        <LoadingSpinner inline size="lg" className="text-themed-accent" />
        <p className="text-sm text-themed-secondary">{message || t('common.loading')}</p>
        {submessage && <p className="text-xs text-themed-muted">{submessage}</p>}
      </div>
    );
  }

  // Shared shimmer supplies gradient + sweep + reduced-motion; keep local radius/dims.
  const block = 'skeleton-shimmer rounded';
  return (
    <div className="flex flex-col gap-3 py-2" role="status" aria-live="polite" aria-busy="true">
      <span className="sr-only">{message || t('common.loading')}</span>
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="flex items-center gap-3">
          <div className={`${block} h-9 w-9 flex-shrink-0`} />
          <div className="flex-1 min-w-0 flex flex-col gap-1.5">
            <div className={`${block} h-3.5 ${i % 2 === 0 ? 'w-2/5' : 'w-1/2'}`} />
            <div className={`${block} h-3 w-1/4`} />
          </div>
          <div className={`${block} h-3.5 w-16 flex-shrink-0`} />
        </div>
      ))}
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
  /** 'panel' renders the dashboard ring-icon block (.empty-state family) */
  variant?: 'plain' | 'panel';
}

/**
 * Standardized empty state for management cards and dashboard panels
 */
export const EmptyState: React.FC<EmptyStateProps> = ({
  icon: Icon,
  title,
  subtitle,
  action,
  variant = 'plain'
}) => {
  if (variant === 'panel') {
    return (
      <div className="empty-state">
        <div className="empty-icon">
          <div className="empty-icon-bg" />
          {Icon && <Icon size={24} />}
        </div>
        <div className="empty-title">{title}</div>
        {subtitle && <div className="empty-desc">{subtitle}</div>}
        {action && <div className="mt-4">{action}</div>}
      </div>
    );
  }
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
export const ReadOnlyBadge: React.FC<ReadOnlyBadgeProps> = ({ message }) => {
  const { t } = useTranslation();

  return (
    <div className="flex items-center justify-center py-4">
      <span className="themed-badge status-badge-warning flex items-center gap-1.5">
        <Lock className="w-3 h-3" />
        {message || t('ui.managerCard.readOnly')}
      </span>
    </div>
  );
};

import React from 'react';
import Badge from '@components/ui/Badge';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { useTranslation } from 'react-i18next';

// ============================================================================
// LOADING STATE
// ============================================================================

interface LoadingStateProps {
  message?: string;
  submessage?: string;
  /**
   * 'skeleton' renders placeholders that mirror a supplied content shape.
   * 'spinner' keeps the centered spinner for spot loads with no supplied shape.
   */
  variant?: 'skeleton' | 'spinner';
  /** Content structure the placeholders should reserve while data loads. */
  shape?:
    | 'list'
    | 'rows'
    | 'table'
    | 'cards'
    | 'fields'
    | 'form'
    | 'chart'
    | 'calendar'
    | 'downloads'
    | 'schedule'
    | 'settings'
    | 'status'
    | 'dashboard';
  /** Skeleton row count (skeleton variant only). */
  rows?: number;
}

/**
 * Shared loading state with layout-specific skeleton compositions. Calls with a shape use a
 * structural placeholder by default; calls without one use the compact spinner treatment.
 */
export const LoadingState: React.FC<LoadingStateProps> = ({
  message,
  submessage,
  variant,
  shape,
  rows = 4
}) => {
  const { t } = useTranslation();
  const selectedVariant = variant ?? (shape ? 'skeleton' : 'spinner');
  const selectedShape = shape ?? 'list';

  if (selectedVariant === 'spinner') {
    return (
      <div className="flex flex-col items-center justify-center py-8 gap-3">
        <LoadingSpinner inline size="lg" className="text-themed-accent" />
        <p className="text-sm text-themed-secondary">{message || t('common.loading')}</p>
        {submessage && <p className="text-xs text-themed-muted">{submessage}</p>}
      </div>
    );
  }

  // Shared surface supplies the placeholder color; keep local radius and dimensions.
  const block = 'skeleton-shimmer rounded';
  const fieldRows =
    selectedShape === 'fields' || selectedShape === 'form'
      ? Array.from({ length: rows }, (_, i) => (
          <div key={i} className="space-y-2">
            <div className={`${block} h-3 w-1/3`} />
            <div className={`${block} h-10 w-full`} />
            {i % 2 === 0 && <div className={`${block} h-3 w-2/3`} />}
          </div>
        ))
      : null;
  return (
    <div className="w-full" role="status" aria-live="polite" aria-busy="true">
      <span className="sr-only">{message || t('common.loading')}</span>
      <div
        className={
          selectedShape === 'dashboard'
            ? 'space-y-4'
            : selectedShape === 'cards'
              ? 'grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3 py-2'
              : 'flex flex-col gap-3 py-2'
        }
        aria-hidden="true"
      >
        {selectedShape === 'dashboard' ? (
          <>
            <div className="flex justify-end items-center gap-2">
              <div className={`${block} hidden md:block h-10 w-32`} />
              <div className={`${block} h-10 w-10`} />
              <div className={`${block} h-10 w-28`} />
            </div>

            <div className="stat-cards-4col">
              {Array.from({ length: 8 }, (_, i) => (
                <div
                  key={i}
                  className="min-h-36 rounded-lg border border-[var(--theme-card-border)] bg-[var(--theme-card-bg)] p-4 flex flex-col"
                >
                  <div className="flex items-start justify-between gap-3">
                    <div className="flex-1 min-w-0 space-y-2">
                      <div className={`${block} h-3 w-2/5`} />
                      <div className={`${block} h-7 ${i % 3 === 0 ? 'w-3/5' : 'w-2/5'}`} />
                      <div className={`${block} h-3 w-1/2`} />
                    </div>
                    <div className={`${block} h-10 w-10 flex-shrink-0`} />
                  </div>
                  <div className={`${block} h-8 w-full mt-auto`} />
                </div>
              ))}
            </div>

            <div className="dashboard-analytics-row">
              <div className="dashboard-analytics-pane dashboard-analytics-pane-chart-expanded">
                <div className="w-full h-full min-h-96 rounded-lg border border-[var(--theme-card-border)] bg-[var(--theme-card-bg)] p-4">
                  <div className="flex items-center justify-between gap-4 mb-6">
                    <div className={`${block} h-5 w-40`} />
                    <div className={`${block} h-9 w-36`} />
                  </div>
                  <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 items-center">
                    <div className={`${block} h-44 w-44 rounded-full mx-auto`} />
                    <div className="space-y-4">
                      {Array.from({ length: 5 }, (_, i) => (
                        <div key={i} className="space-y-2">
                          <div className="flex justify-between gap-3">
                            <div className={`${block} h-3 w-2/5`} />
                            <div className={`${block} h-3 w-12`} />
                          </div>
                          <div className={`${block} h-2 w-full`} />
                        </div>
                      ))}
                    </div>
                  </div>
                </div>
              </div>

              <div className="dashboard-analytics-pane dashboard-analytics-pane-downloads-expanded">
                <div className="w-full h-full min-h-96 rounded-lg border border-[var(--theme-card-border)] bg-[var(--theme-card-bg)] p-4">
                  <div className="flex items-center justify-between gap-4 mb-5">
                    <div className={`${block} h-5 w-36`} />
                    <div className={`${block} h-8 w-20`} />
                  </div>
                  <div className="space-y-3">
                    {Array.from({ length: 5 }, (_, i) => (
                      <div key={i} className="flex items-center gap-3">
                        <div className={`${block} h-10 w-10 flex-shrink-0`} />
                        <div className="flex-1 min-w-0 space-y-2">
                          <div className={`${block} h-3.5 ${i % 2 === 0 ? 'w-3/5' : 'w-1/2'}`} />
                          <div className={`${block} h-3 w-2/5`} />
                        </div>
                        <div className="w-16 flex-shrink-0 space-y-2">
                          <div className={`${block} h-3 w-full`} />
                          <div className={`${block} h-3 w-3/4 ml-auto`} />
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              </div>
            </div>

            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              {Array.from({ length: 2 }, (_, i) => (
                <div
                  key={i}
                  className="min-h-72 rounded-lg border border-[var(--theme-card-border)] bg-[var(--theme-card-bg)] p-4 flex flex-col"
                >
                  <div className="flex items-center justify-between gap-4 mb-6">
                    <div className={`${block} h-5 w-36`} />
                    <div className={`${block} h-8 w-20`} />
                  </div>
                  <div className="flex-1 flex items-end gap-2">
                    {['h-12', 'h-20', 'h-16', 'h-28', 'h-24', 'h-32', 'h-20', 'h-28'].map(
                      (height, barIndex) => (
                        <div key={barIndex} className={`${block} ${height} flex-1`} />
                      )
                    )}
                  </div>
                </div>
              ))}
            </div>

            <div className="rounded-lg border border-[var(--theme-card-border)] bg-[var(--theme-card-bg)] p-4">
              <div className="flex items-center justify-between gap-4 mb-5">
                <div className={`${block} h-5 w-32`} />
                <div className={`${block} h-9 w-24`} />
              </div>
              <div className="space-y-3">
                {Array.from({ length: 5 }, (_, i) => (
                  <div key={i} className="grid grid-cols-5 gap-3 items-center">
                    <div className={`${block} h-3.5 col-span-2`} />
                    <div className={`${block} h-3.5`} />
                    <div className={`${block} h-3.5`} />
                    <div className={`${block} h-3.5`} />
                  </div>
                ))}
              </div>
            </div>
          </>
        ) : selectedShape === 'downloads' ? (
          <>
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
              <div className="flex items-center gap-2">
                <div className={`${block} h-10 w-28`} />
                <div className={`${block} h-10 w-28`} />
              </div>
              <div className={`${block} h-10 w-24`} />
            </div>
            <div className="space-y-2">
              {Array.from({ length: rows }, (_, i) => (
                <div
                  key={i}
                  className="min-h-20 rounded-xl bg-[var(--theme-bg-secondary)] p-4 flex items-center gap-4"
                >
                  <div className={`${block} h-10 w-10 flex-shrink-0`} />
                  <div className="flex-1 min-w-0 space-y-2">
                    <div className={`${block} h-4 ${i % 2 === 0 ? 'w-2/5' : 'w-1/2'}`} />
                    <div className={`${block} h-3 w-3/5`} />
                  </div>
                  <div className="w-24 flex-shrink-0 space-y-2">
                    <div className={`${block} h-4 w-full`} />
                    <div className={`${block} h-3 w-1/2 ml-auto`} />
                  </div>
                </div>
              ))}
            </div>
            <div className="grid grid-cols-2 md:grid-cols-4 gap-3 pt-3 border-t border-[var(--theme-border-secondary)]">
              {Array.from({ length: 4 }, (_, i) => (
                <div key={i} className="space-y-2">
                  <div className={`${block} h-4 w-12`} />
                  <div className={`${block} h-3 ${i % 2 === 0 ? 'w-20' : 'w-24'}`} />
                </div>
              ))}
            </div>
          </>
        ) : selectedShape === 'calendar' ? (
          <>
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-end gap-3">
              <div className={`${block} h-10 w-48`} />
              <div className={`${block} h-10 w-28`} />
            </div>
            <div className="rounded-lg border border-[var(--theme-card-border)] bg-[var(--theme-card-bg)] p-4 space-y-4">
              <div className="flex items-center justify-between gap-4">
                <div className={`${block} h-9 w-9`} />
                <div className={`${block} h-5 w-36`} />
                <div className={`${block} h-9 w-9`} />
              </div>
              <div className="grid grid-cols-7 gap-1 sm:gap-2">
                {Array.from({ length: 7 }, (_, i) => (
                  <div key={i} className={`${block} h-3 w-3/5 mx-auto`} />
                ))}
                {Array.from({ length: 35 }, (_, i) => (
                  <div
                    key={i}
                    className="min-h-12 sm:min-h-16 rounded border border-[var(--theme-border-secondary)] p-1 sm:p-2"
                  >
                    <div className={`${block} h-3 w-3 sm:w-5`} />
                    {i % 6 === 0 && <div className={`${block} h-2 w-full mt-3`} />}
                  </div>
                ))}
              </div>
            </div>
          </>
        ) : selectedShape === 'schedule' ? (
          <>
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
              <div className="flex items-center gap-2">
                <div className={`${block} h-5 w-1`} />
                <div className={`${block} h-4 w-32`} />
              </div>
              <div className="flex items-center gap-2">
                <div className={`${block} h-10 w-24`} />
                <div className={`${block} h-10 w-36`} />
              </div>
            </div>
            <div className="overflow-hidden rounded-lg border border-[var(--theme-border-primary)] bg-[var(--theme-card-bg)]">
              <div className="grid grid-cols-[minmax(0,1fr)_6rem] sm:grid-cols-5 gap-3 px-4 py-3 border-b border-[var(--theme-border-secondary)]">
                <div className={`${block} h-3 w-2/5`} />
                <div className={`${block} hidden sm:block h-3 w-3/5`} />
                <div className={`${block} hidden sm:block h-3 w-3/5`} />
                <div className={`${block} hidden sm:block h-3 w-3/5`} />
                <div />
              </div>
              {Array.from({ length: rows }, (_, i) => (
                <div
                  key={i}
                  className="grid grid-cols-[minmax(0,1fr)_6rem] sm:grid-cols-5 gap-3 items-center min-h-16 px-4 py-2 border-b last:border-b-0 border-[var(--theme-border-secondary)]"
                >
                  <div className="flex items-center gap-3 min-w-0">
                    <div className={`${block} h-8 w-8 flex-shrink-0`} />
                    <div className={`${block} h-3.5 ${i % 2 === 0 ? 'w-3/5' : 'w-1/2'}`} />
                  </div>
                  <div className={`${block} hidden sm:block h-3.5 w-4/5`} />
                  <div className={`${block} hidden sm:block h-3.5 w-3/5`} />
                  <div className={`${block} hidden sm:block h-9 w-full`} />
                  <div className={`${block} h-9 w-full`} />
                </div>
              ))}
            </div>
            <div className="min-h-32 rounded-lg border border-[var(--theme-card-border)] bg-[var(--theme-card-bg)] p-4 flex flex-col gap-3">
              <div className="flex items-center justify-between gap-3">
                <div className="flex items-center gap-3 min-w-0 flex-1">
                  <div className={`${block} h-9 w-9 flex-shrink-0`} />
                  <div className={`${block} h-4 w-2/5`} />
                </div>
                <div className={`${block} h-9 w-24`} />
              </div>
              <div className={`${block} h-3 w-3/5`} />
              <div className={`${block} h-3 w-2/5`} />
            </div>
          </>
        ) : selectedShape === 'settings' ? (
          <>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              {Array.from({ length: 2 }, (_, i) => (
                <div
                  key={i}
                  className="min-h-40 rounded-lg bg-[var(--theme-bg-tertiary)] p-4 flex flex-col"
                >
                  <div className="flex items-center gap-2 pb-3 mb-4 border-b border-[var(--theme-border-secondary)]">
                    <div className={`${block} h-6 w-6`} />
                    <div className={`${block} h-4 w-28`} />
                  </div>
                  <div className="flex items-start gap-3">
                    <div className={`${block} h-5 w-5 flex-shrink-0`} />
                    <div className="flex-1 space-y-2">
                      <div className={`${block} h-4 w-2/5`} />
                      <div className={`${block} h-3 w-4/5`} />
                      {i === 1 && <div className={`${block} h-10 w-full mt-3`} />}
                    </div>
                  </div>
                </div>
              ))}
            </div>
            <div className="min-h-32 rounded-lg bg-[var(--theme-bg-tertiary)] p-4">
              <div className="flex items-center gap-2 pb-3 mb-4 border-b border-[var(--theme-border-secondary)]">
                <div className={`${block} h-6 w-6`} />
                <div className={`${block} h-4 w-24`} />
              </div>
              <div className="flex flex-col sm:flex-row gap-3">
                <div className={`${block} h-9 flex-1`} />
                <div className={`${block} h-9 w-full sm:w-32`} />
              </div>
            </div>
          </>
        ) : selectedShape === 'status' ? (
          <>
            <div className="min-h-64 rounded-lg border border-[var(--theme-card-border)] bg-[var(--theme-card-bg)] p-5 flex flex-col">
              <div className="flex items-center justify-between gap-4">
                <div className={`${block} h-5 w-36`} />
                <div className={`${block} h-9 w-48`} />
              </div>
              <div className={`${block} h-16 w-16 rounded-full mx-auto mt-8`} />
              <div className={`${block} h-6 w-44 mx-auto mt-4`} />
              <div className={`${block} h-3 w-3/5 mx-auto mt-3`} />
              <div className={`${block} h-3 w-full mt-auto`} />
            </div>
            <div className="space-y-3">
              <div className={`${block} h-3 w-28`} />
              <div className="overflow-hidden rounded-lg border border-[var(--theme-border-secondary)]">
                {Array.from({ length: rows }, (_, i) => (
                  <div
                    key={i}
                    className="min-h-14 px-4 flex items-center gap-3 border-b last:border-b-0 border-[var(--theme-border-secondary)]"
                  >
                    <div className={`${block} h-8 w-8 flex-shrink-0`} />
                    <div className={`${block} h-3.5 ${i % 2 === 0 ? 'w-2/5' : 'w-1/2'}`} />
                    <div className={`${block} h-6 w-20 ml-auto`} />
                  </div>
                ))}
              </div>
            </div>
            <div className="space-y-4">
              {Array.from({ length: 2 }, (_, i) => (
                <div key={i} className="space-y-3">
                  <div className={`${block} h-3 w-24`} />
                  <div className="min-h-32 rounded-lg border border-[var(--theme-card-border)] bg-[var(--theme-card-bg)] p-4 space-y-3">
                    <div className={`${block} h-4 w-2/5`} />
                    <div className={`${block} h-3 w-4/5`} />
                    <div className={`${block} h-9 w-28`} />
                  </div>
                </div>
              ))}
            </div>
          </>
        ) : selectedShape === 'chart' ? (
          <>
            <div className="flex items-center justify-between gap-4">
              <div className={`${block} h-5 w-40`} />
              <div className={`${block} h-9 w-32`} />
            </div>
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 items-center flex-1 min-h-72">
              <div className={`${block} h-44 w-44 rounded-full mx-auto`} />
              <div className="space-y-4">
                {Array.from({ length: rows }, (_, i) => (
                  <div key={i} className="space-y-2">
                    <div className="flex justify-between gap-3">
                      <div className={`${block} h-3.5 ${i % 2 === 0 ? 'w-2/5' : 'w-1/2'}`} />
                      <div className={`${block} h-3.5 w-12`} />
                    </div>
                    <div className={`${block} h-2 w-full`} />
                  </div>
                ))}
              </div>
            </div>
          </>
        ) : selectedShape === 'table' ? (
          <div className="overflow-hidden rounded-lg border border-[var(--theme-border-well)] bg-[var(--theme-bg-tertiary)]">
            <div className="grid grid-cols-[minmax(0,1fr)_5rem] sm:grid-cols-5 gap-3 px-3 py-2 border-b border-[var(--theme-border-secondary)]">
              <div className={`${block} h-3 col-span-1 sm:col-span-2 w-2/5`} />
              <div className={`${block} hidden sm:block h-3 w-3/4`} />
              <div className={`${block} hidden sm:block h-3 w-3/4`} />
              <div className={`${block} h-3 w-3/4`} />
            </div>
            {Array.from({ length: rows }, (_, i) => (
              <div
                key={i}
                className="grid grid-cols-[minmax(0,1fr)_5rem] sm:grid-cols-5 gap-3 items-center min-h-11 px-3 py-2"
              >
                <div className="col-span-1 sm:col-span-2 flex items-center gap-3 min-w-0">
                  <div className={`${block} h-8 w-8 flex-shrink-0`} />
                  <div className={`${block} h-3.5 ${i % 2 === 0 ? 'w-3/5' : 'w-1/2'}`} />
                </div>
                <div className={`${block} hidden sm:block h-3.5 w-4/5`} />
                <div className={`${block} hidden sm:block h-3.5 w-3/5`} />
                <div className={`${block} h-7 w-full`} />
              </div>
            ))}
          </div>
        ) : selectedShape === 'cards' ? (
          Array.from({ length: rows }, (_, i) => (
            <div
              key={i}
              className="min-h-32 rounded-lg border border-[var(--theme-card-border)] bg-[var(--theme-card-bg)] p-4 flex flex-col"
            >
              <div className="flex items-start gap-3">
                <div className={`${block} h-10 w-10 flex-shrink-0`} />
                <div className="flex-1 min-w-0 space-y-2">
                  <div className={`${block} h-4 ${i % 2 === 0 ? 'w-1/2' : 'w-3/5'}`} />
                  <div className={`${block} h-3 w-1/3`} />
                </div>
                <div className={`${block} h-7 w-16 flex-shrink-0`} />
              </div>
              <div className={`${block} h-3 w-full mt-auto`} />
            </div>
          ))
        ) : selectedShape === 'fields' ? (
          fieldRows
        ) : selectedShape === 'form' ? (
          <>
            <div className="grid grid-cols-1 gap-4">{fieldRows}</div>
            <div className={`${block} h-9 w-28 self-end`} />
          </>
        ) : selectedShape === 'rows' ? (
          Array.from({ length: rows }, (_, i) => (
            <div
              key={i}
              className="min-h-20 rounded-lg border border-[var(--theme-border-well)] bg-[var(--theme-bg-tertiary)] p-4 flex items-center gap-3"
            >
              <div className={`${block} h-9 w-9 flex-shrink-0`} />
              <div className="flex-1 min-w-0 flex flex-col gap-2">
                <div className={`${block} h-3.5 ${i % 2 === 0 ? 'w-2/5' : 'w-1/2'}`} />
                <div className={`${block} h-3 w-3/5`} />
              </div>
              <div className={`${block} h-7 w-20 flex-shrink-0`} />
            </div>
          ))
        ) : (
          <div className="overflow-hidden rounded-lg border border-[var(--theme-border-well)] bg-[var(--theme-bg-tertiary)]">
            {Array.from({ length: rows }, (_, i) => (
              <div
                key={i}
                className="min-h-14 px-3 py-2 flex items-center gap-3 border-b last:border-b-0 border-[var(--theme-border-secondary)]"
              >
                <div className={`${block} h-9 w-9 flex-shrink-0`} />
                <div className="flex-1 min-w-0 flex flex-col gap-1.5">
                  <div className={`${block} h-3.5 ${i % 2 === 0 ? 'w-2/5' : 'w-1/2'}`} />
                  <div className={`${block} h-3 w-1/4`} />
                </div>
                <div className={`${block} h-3.5 w-16 flex-shrink-0`} />
              </div>
            ))}
          </div>
        )}
      </div>
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
  /**
   * 'panel' renders the dashboard ring-icon block (.empty-state family). 'text' renders a bare
   * centered sentence with no icon, for a list that only ever needs one line ("No games found").
   */
  variant?: 'plain' | 'panel' | 'text';
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
  if (variant === 'text') {
    return <p className="py-4 text-center text-sm text-themed-muted">{title}</p>;
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
      <Badge variant="warning">{message || t('ui.managerCard.readOnly')}</Badge>
    </div>
  );
};

import React from 'react';
import { useTranslation } from 'react-i18next';
import { Loader2, ChevronDown, ChevronUp } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Tooltip } from '@components/ui/Tooltip';

export interface ExpandableItemStat {
  icon: React.ComponentType<{ className?: string }>;
  value: string | number;
  label: string;
  labelCount?: number;
}

interface ExpandableItemCardProps {
  id: number | string;
  title: string;
  titleClassName?: string;
  subtitle?: React.ReactNode;
  stats: ExpandableItemStat[];
  datasources?: string[];
  isExpanded: boolean;
  isExpanding: boolean;
  isRemoving: boolean;
  isAnyRemovalRunning: boolean;
  isAuthenticated: boolean;
  cacheReadOnly: boolean;
  dockerSocketAvailable: boolean;
  checkingPermissions: boolean;
  onToggleDetails: (id: number | string) => void;
  onRemove: () => void;
  removeTooltip: string;
  children?: React.ReactNode;
}

const ExpandableItemCard: React.FC<ExpandableItemCardProps> = ({
  id,
  title,
  titleClassName,
  subtitle,
  stats,
  datasources,
  isExpanded,
  isExpanding,
  isRemoving,
  isAnyRemovalRunning,
  isAuthenticated,
  cacheReadOnly,
  dockerSocketAvailable,
  checkingPermissions,
  onToggleDetails,
  onRemove,
  removeTooltip,
  children
}) => {
  const { t } = useTranslation();

  return (
    <div className="rounded-lg border bg-themed-tertiary border-themed-secondary">
      <div className="flex items-center gap-2 p-3">
        <Button
          onClick={() => onToggleDetails(id)}
          variant="subtle"
          size="sm"
          className="flex-shrink-0"
          disabled={isExpanding}
        >
          {isExpanding ? (
            <Loader2 className="w-4 h-4 animate-spin" />
          ) : isExpanded ? (
            <ChevronUp className="w-4 h-4" />
          ) : (
            <ChevronDown className="w-4 h-4" />
          )}
        </Button>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 mb-1 flex-wrap">
            <h4 className={titleClassName || "text-themed-primary font-semibold break-words"}>{title}</h4>
            {subtitle && subtitle}
          </div>
          <div className="flex items-center gap-3 text-xs text-themed-muted flex-wrap">
            {stats.map((stat, idx) => {
              const Icon = stat.icon;
              return (
                <span key={idx} className="flex items-center gap-1">
                  <Icon className="w-3 h-3" />
                  <strong className="text-themed-primary">
                    {stat.value}
                  </strong>{' '}
                  {stat.labelCount !== undefined
                    ? t(stat.label, { count: stat.labelCount })
                    : t(stat.label)}
                </span>
              );
            })}
            {datasources && datasources.length > 0 && (
              <span className="flex items-center gap-1">
                {datasources.map((ds) => (
                  <span
                    key={ds}
                    className="px-1.5 py-0.5 rounded text-[10px] font-medium capitalize bg-themed-accent-subtle text-themed-accent"
                  >
                    {ds}
                  </span>
                ))}
              </span>
            )}
          </div>
        </div>
        <Tooltip content={removeTooltip}>
          <Button
            onClick={onRemove}
            disabled={isAnyRemovalRunning || !isAuthenticated || cacheReadOnly || !dockerSocketAvailable || checkingPermissions}
            variant="filled"
            color="red"
            size="sm"
            loading={isRemoving}
            title={
              cacheReadOnly
                ? t('management.gameDetection.cacheReadOnlyShort')
                : !dockerSocketAvailable
                  ? t('management.gameDetection.dockerSocketRequired')
                  : undefined
            }
          >
            {isRemoving ? t('management.gameDetection.removing') : t('common.remove')}
          </Button>
        </Tooltip>
      </div>

      {/* Loading State for Expansion */}
      {isExpanding && (
        <div className="border-t px-3 py-4 flex items-center justify-center border-themed-secondary">
          <div className="flex items-center gap-2 text-themed-muted">
            <Loader2 className="w-4 h-4 animate-spin" />
            <span className="text-sm">{t('management.gameDetection.loadingDetails')}</span>
          </div>
        </div>
      )}

      {/* Expandable Details Section */}
      {isExpanded && !isExpanding && (
        <div className="border-t px-3 py-3 space-y-3 border-themed-secondary">
          {children}
        </div>
      )}
    </div>
  );
};

export default ExpandableItemCard;

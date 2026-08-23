import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, Trash2 } from 'lucide-react';
import '../managementSectionContent.css';
import { Button } from '@components/ui/Button';
import { Checkbox } from '@components/ui/Checkbox';
import { Tooltip } from '@components/ui/Tooltip';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import Badge from '@components/ui/Badge';
import { GameImage } from '../../../common/GameImage';
import { useAvailableGameImages } from '@hooks/useAvailableGameImages';
import { nameKeyedImageKey } from '@utils/gameBannerSlug';
import { useCacheRemovalActive } from '@hooks/useCacheRemovalActive';
import { useDiskObjectCapability } from '@hooks/useDiskObjectCapability';
import { rowToggleHandlers } from '@utils/rowToggle';

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
  gameAppId?: string | number;
  epicAppId?: string;
  service?: string;
  stats: ExpandableItemStat[];
  datasources?: string[];
  isExpanded: boolean;
  isRemoving: boolean;
  isAdmin: boolean;
  diskActionBlocked?: boolean;
  nginxReopenAvailable: boolean;
  nginxReopenUnavailableMessage: string;
  hasExpandableContent?: boolean;
  onToggleDetails: (id: number | string) => void;
  onRemove: () => void;
  removeTooltip: string;
  /** When true, a selection checkbox renders as the first child of the header row. */
  selectable?: boolean;
  selected?: boolean;
  onSelectToggle?: () => void;
  selectLabel?: string;
  children?: React.ReactNode;
}

const ExpandableItemCard: React.FC<ExpandableItemCardProps> = ({
  id,
  title,
  titleClassName,
  subtitle,
  gameAppId,
  epicAppId,
  service,
  stats,
  datasources,
  isExpanded,
  isRemoving,
  isAdmin,
  diskActionBlocked = false,
  nginxReopenAvailable,
  nginxReopenUnavailableMessage,
  hasExpandableContent = true,
  onToggleDetails,
  onRemove,
  removeTooltip,
  selectable = false,
  selected = false,
  onSelectToggle,
  selectLabel,
  children
}) => {
  const { t } = useTranslation();
  // Any running/queued removal in the game-cache domain disables every per-item
  // Remove button - single removes and Remove All gate together.
  const isCacheRemovalActive = useCacheRemovalActive();
  // Disk-level object removal needs one resolved cache-key scheme for every enabled datasource
  // because an entity can span datasource roots.
  const { available: diskObjectsAvailable, denialReason: diskObjectDenialReason } =
    useDiskObjectCapability();
  const [imageError, setImageError] = useState(false);
  const availableImages = useAvailableGameImages();

  const handleImageFinalError = (_gameAppId: string) => {
    setImageError(true);
  };

  const isEpic = service === 'epicgames';
  const nameKeyed = nameKeyedImageKey(service, title);
  const imageId = nameKeyed ? nameKeyed.slug : isEpic ? epicAppId : String(gameAppId ?? '');
  const showImage = !!imageId && availableImages.has(imageId) && !imageError;
  const isUnknownGame = title.startsWith('Unknown Game');
  const actionTooltip = !diskObjectsAvailable
    ? (diskObjectDenialReason ?? t('management.capability.diskObjectsUnavailable'))
    : diskActionBlocked
      ? t('initialization.permissionsCheck.hasErrors')
      : !nginxReopenAvailable
        ? nginxReopenUnavailableMessage
        : removeTooltip;

  return (
    <div>
      <div
        className={`mgmt-row${hasExpandableContent ? ' mgmt-row--interactive focus-ring--inset' : ''}`}
        aria-expanded={hasExpandableContent ? isExpanded : undefined}
        {...(hasExpandableContent ? rowToggleHandlers(() => onToggleDetails(id)) : {})}
      >
        {selectable && (
          <Checkbox
            checked={selected}
            onChange={() => onSelectToggle?.()}
            disabled={isRemoving || isCacheRemovalActive}
            aria-label={selectLabel}
            className="flex-shrink-0"
          />
        )}
        <div className="mgmt-row__body game-card-content">
          <div className="game-card-titleline">
            {showImage && (
              <GameImage
                gameAppId={nameKeyed ? undefined : gameAppId}
                epicAppId={isEpic ? epicAppId : undefined}
                nameKeyedService={nameKeyed ? nameKeyed.service : undefined}
                nameKeyedSlug={nameKeyed ? nameKeyed.slug : undefined}
                alt={title}
                className="game-card-image hidden sm:block"
                loading="lazy"
                onError={handleImageFinalError}
              />
            )}
            <div className="min-w-0 flex-1">
              <div className="game-card-heading">
                <h4 className={titleClassName || 'mgmt-row__title'}>{title}</h4>
                {isUnknownGame && (
                  <Badge variant="warning">{t('management.gameDetection.unknownGameBadge')}</Badge>
                )}
                {subtitle && subtitle}
              </div>
              <div className="mgmt-row__meta game-card-meta">
                {stats.map((stat, idx) => {
                  const Icon = stat.icon;
                  return (
                    <span key={idx}>
                      <Icon className="game-card-stat-icon" />
                      <strong>{stat.value}</strong>{' '}
                      {stat.labelCount !== undefined
                        ? t(stat.label, { count: stat.labelCount })
                        : t(stat.label)}
                    </span>
                  );
                })}
                {datasources &&
                  datasources.length > 0 &&
                  datasources.map((ds) => (
                    <span
                      key={ds}
                      className="themed-badge bg-themed-accent-subtle text-themed-accent"
                    >
                      {ds}
                    </span>
                  ))}
              </div>
            </div>
          </div>
        </div>
        <div className="mgmt-row__actions game-card-actions">
          <Tooltip content={actionTooltip}>
            <Button
              type="button"
              onClick={onRemove}
              awaitPermissions
              loading={isRemoving}
              disabled={
                !isAdmin ||
                diskActionBlocked ||
                !nginxReopenAvailable ||
                isCacheRemovalActive ||
                !diskObjectsAvailable
              }
              variant="filled"
              color="destructive"
              size="sm"
              className="btn-icon-square btn-icon-square--sm pointer-target-44"
              aria-label={t('common.remove')}
            >
              <Trash2 className="w-4 h-4" />
            </Button>
          </Tooltip>
          {hasExpandableContent && (
            <Button
              type="button"
              variant="accordion"
              size="sm"
              open={isExpanded}
              className="btn-icon-square btn-icon-square--sm pointer-target-44"
              onClick={(e) => {
                e.stopPropagation();
                onToggleDetails(id);
              }}
              aria-label={
                isExpanded ? t('ui.accordion.collapseSection') : t('ui.accordion.expandSection')
              }
              aria-expanded={isExpanded}
            >
              <ChevronDown
                className={`w-4 h-4 transition duration-200 ease-out${
                  isExpanded ? ' rotate-180 text-themed-accent' : ' rotate-0 text-themed-muted'
                }`}
              />
            </Button>
          )}
        </div>
      </div>

      <CollapsibleRegion
        open={hasExpandableContent && isExpanded}
        contentClassName="mgmt-row-detail game-card-detail"
      >
        {children}
      </CollapsibleRegion>
    </div>
  );
};

export default ExpandableItemCard;

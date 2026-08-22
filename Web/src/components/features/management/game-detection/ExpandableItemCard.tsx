import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, ChevronUp, Trash2 } from 'lucide-react';
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

  // The dedicated chevron button already toggles the same details section, so the whole row
  // toggles too - the nested-control guard lets the button (and the selection checkbox) still
  // handle their own clicks. Only wired up when there is something to expand: with no expandable
  // content the row has nothing to toggle. [28]
  return (
    <div>
      <div
        className={`mgmt-row${hasExpandableContent ? ' mgmt-row--interactive focus-ring--inset' : ''}`}
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
        <div className="flex items-center gap-2 flex-1 min-w-0 game-card-content">
          {hasExpandableContent && (
            <Button
              onClick={() => onToggleDetails(id)}
              variant="filled"
              color="secondary"
              size="sm"
              className="flex-shrink-0 min-h-[44px] sm:min-h-0"
            >
              {isExpanded ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
            </Button>
          )}
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
          <div className="flex-1 min-w-0">
            <div className="flex items-center gap-2 mb-1 flex-wrap">
              <h4 className={titleClassName || 'text-themed-primary font-semibold break-words'}>
                {title}
              </h4>
              {isUnknownGame && (
                <Badge variant="warning">{t('management.gameDetection.unknownGameBadge')}</Badge>
              )}
              {subtitle && subtitle}
            </div>
            <div className="flex items-center gap-3 text-xs text-themed-muted flex-wrap">
              {stats.map((stat, idx) => {
                const Icon = stat.icon;
                return (
                  <span key={idx} className="flex items-center gap-1">
                    <Icon className="w-3 h-3" />
                    <strong className="text-themed-primary">{stat.value}</strong>{' '}
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
                      className="themed-badge bg-themed-accent-subtle text-themed-accent"
                    >
                      {ds}
                    </span>
                  ))}
                </span>
              )}
            </div>
          </div>
        </div>
        <Tooltip content={actionTooltip}>
          <Button
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
            className="flex-shrink-0 min-h-[44px] sm:min-h-0"
          >
            {isRemoving ? (
              // Hide the label on mobile so the button stays compact next to the
              // spinner; the spinner (from `loading`) is the mobile removing signal.
              <span className="hidden sm:inline">{t('management.gameDetection.removing')}</span>
            ) : (
              <>
                <Trash2 className="w-4 h-4 sm:hidden" />
                <span className="hidden sm:inline">{t('common.remove')}</span>
              </>
            )}
          </Button>
        </Tooltip>
      </div>

      {/* Expandable Details Section */}
      <CollapsibleRegion
        open={hasExpandableContent && isExpanded}
        contentClassName="mgmt-row-detail space-y-3"
      >
        {children}
      </CollapsibleRegion>
    </div>
  );
};

export default ExpandableItemCard;

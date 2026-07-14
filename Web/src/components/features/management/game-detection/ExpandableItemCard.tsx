import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, ChevronUp, Trash2 } from 'lucide-react';
import '../managementSectionContent.css';
import { Button } from '@components/ui/Button';
import { Checkbox } from '@components/ui/Checkbox';
import { Tooltip } from '@components/ui/Tooltip';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { useDirectoryPermissionsContext } from '@contexts/useDirectoryPermissionsContext';
import { GameImage } from '../../../common/GameImage';
import { useAvailableGameImages } from '@hooks/useAvailableGameImages';
import { nameKeyedImageKey } from '@utils/gameBannerSlug';
import { useCacheRemovalActive } from '@hooks/useCacheRemovalActive';

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
  dockerSocketAvailable: boolean;
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
  dockerSocketAvailable,
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
  const { cacheReadOnly } = useDirectoryPermissionsContext();
  // Any running/queued removal in the game-cache domain disables every per-item
  // Remove button - single removes and Remove All gate together.
  const isCacheRemovalActive = useCacheRemovalActive();
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

  return (
    <div>
      <div className="mgmt-row mgmt-row--interactive">
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
              color="gray"
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
                <span className="text-xs font-medium px-2 py-0.5 rounded bg-themed-elevated text-themed-warning">
                  {t('management.gameDetection.unknownGameBadge')}
                </span>
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
                      className="px-1.5 py-0.5 rounded text-[10px] font-medium capitalize bg-themed-accent-subtle text-themed-accent"
                    >
                      {ds}
                    </span>
                  ))}
                </span>
              )}
            </div>
          </div>
        </div>
        <Tooltip content={removeTooltip}>
          <Button
            onClick={onRemove}
            awaitPermissions
            loading={isRemoving}
            disabled={!isAdmin || cacheReadOnly || !dockerSocketAvailable || isCacheRemovalActive}
            variant="filled"
            color="red"
            size="sm"
            className="flex-shrink-0 min-h-[44px] sm:min-h-0"
            title={
              cacheReadOnly
                ? t('management.gameDetection.cacheReadOnlyShort')
                : !dockerSocketAvailable
                  ? t('management.gameDetection.dockerSocketRequired')
                  : undefined
            }
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

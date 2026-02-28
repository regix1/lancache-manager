import React from 'react';
import {
  Moon,
  Sun,
  Lock,
  MoreVertical,
  Check,
  Eye,
  EyeOff,
  Edit,
  Download,
  Trash2,
  Globe
} from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { type Theme } from './types';
import {
  ActionMenu,
  ActionMenuItem,
  ActionMenuDivider,
  ActionMenuDangerItem
} from '@components/ui/ActionMenu';
import { Tooltip } from '@components/ui/Tooltip';

interface ThemeCardProps {
  theme: Theme;
  isActive: boolean;
  isPreviewing: boolean;
  isSystem: boolean;
  isAdmin: boolean;
  isGuest: boolean;
  themeActionMenu: string | null;
  currentMenuId: string;
  onApplyTheme: (themeId: string) => void;
  onPreview: (themeId: string) => void;
  onEdit: (theme: Theme) => void;
  onExport: (theme: Theme) => void;
  onDelete: (themeId: string, themeName: string) => void;
  onMenuToggle: (themeId: string | null) => void;
}

export const ThemeCard: React.FC<ThemeCardProps> = ({
  theme,
  isActive,
  isPreviewing,
  isSystem,
  isAdmin,
  isGuest,
  themeActionMenu,
  currentMenuId,
  onApplyTheme,
  onPreview,
  onEdit,
  onExport,
  onDelete,
  onMenuToggle
}) => {
  const { t } = useTranslation();
  const isMenuOpen = themeActionMenu === currentMenuId;

  const colorPreview = [
    { label: t('management.themes.colorLabels.primary'), color: theme.colors.primaryColor },
    { label: t('management.themes.colorLabels.secondary'), color: theme.colors.secondaryColor },
    { label: t('management.themes.colorLabels.accent'), color: theme.colors.accentColor },
    { label: t('management.themes.colorLabels.background'), color: theme.colors.bgPrimary },
    { label: t('management.themes.colorLabels.text'), color: theme.colors.textPrimary }
  ].filter((item) => Boolean(item.color)) as { label: string; color: string }[];

  return (
    <div
      className={`rounded-lg p-4 transition-all hover:shadow-md themed-card relative group isolate bg-themed-secondary border ${
        isActive ? 'border-primary' : isPreviewing ? 'border-warning' : 'border-themed-secondary'
      }`}
    >
      {/* Status Badge - Top Right */}
      {(isActive || isPreviewing) && (
        <div
          className={`absolute -top-2 -right-2 px-2 py-0.5 text-xs font-medium rounded-full text-white ${
            isActive ? 'bg-primary' : 'bg-warning'
          }`}
        >
          {isActive ? t('management.themes.activeBadge') : t('management.themes.previewBadge')}
        </div>
      )}

      {/* Theme Header */}
      <div className="flex items-start justify-between mb-3">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 mb-1">
            <span className="font-semibold text-themed-primary text-sm truncate">
              {theme.meta.name}
            </span>
            {theme.meta.isDark ? (
              <Moon className="w-3.5 h-3.5 text-themed-muted flex-shrink-0" />
            ) : (
              <Sun className="w-3.5 h-3.5 icon-yellow flex-shrink-0" />
            )}
            {isSystem && <Lock className="w-3 h-3 text-themed-muted flex-shrink-0" />}
          </div>

          {/* Badges Row */}
          <div className="flex flex-wrap gap-1 mb-2">
            {theme.meta.isCommunityTheme && (
              <span className="inline-flex items-center gap-1 px-1.5 py-0.5 text-xs rounded bg-themed-info text-info">
                <Globe className="w-2.5 h-2.5" />
                {t('management.themes.communityBadge')}
              </span>
            )}
            {theme.meta.basedOn && (
              <span className="px-1.5 py-0.5 text-xs rounded bg-themed-accent-subtle text-themed-accent">
                {t('management.themes.customBadge')}
              </span>
            )}
          </div>

          {theme.meta.basedOn && (
            <p className="text-xs text-themed-muted mb-1">
              {t('management.themes.basedOn')} {theme.meta.basedOn}
            </p>
          )}
          {theme.meta.description && (
            <p className="text-xs text-themed-muted line-clamp-2 mb-1">{theme.meta.description}</p>
          )}
          <div className="flex items-center gap-2 text-xs text-themed-muted">
            {theme.meta.author && (
              <span>
                {t('management.themes.by')} {theme.meta.author}
              </span>
            )}
            {theme.meta.version && (
              <span className="px-1.5 py-0.5 rounded bg-themed-tertiary">
                v{theme.meta.version}
              </span>
            )}
          </div>
        </div>

        {/* Action Menu Button */}
        <ActionMenu
          isOpen={isMenuOpen}
          onClose={() => onMenuToggle(null)}
          trigger={
            <button
              onClick={() => onMenuToggle(themeActionMenu === currentMenuId ? null : currentMenuId)}
              className={`p-1.5 rounded-lg hover:bg-themed-hover transition-colors opacity-0 group-hover:opacity-100 ${
                isMenuOpen ? 'bg-themed-hover' : 'bg-transparent'
              }`}
            >
              <MoreVertical className="w-4 h-4 text-themed-muted" />
            </button>
          }
        >
          {!isGuest && !isActive && (
            <ActionMenuItem
              onClick={() => {
                onApplyTheme(currentMenuId);
                onMenuToggle(null);
              }}
              icon={<Check className="w-3.5 h-3.5" />}
            >
              {t('management.themes.actions.applyTheme')}
            </ActionMenuItem>
          )}
          {!isGuest && (
            <ActionMenuItem
              onClick={() => {
                onPreview(currentMenuId);
                onMenuToggle(null);
              }}
              icon={
                isPreviewing ? <EyeOff className="w-3.5 h-3.5" /> : <Eye className="w-3.5 h-3.5" />
              }
            >
              {isPreviewing
                ? t('management.themes.actions.stopPreview')
                : t('management.themes.actions.preview')}
            </ActionMenuItem>
          )}
          {!isSystem && isAdmin && (
            <ActionMenuItem
              onClick={() => {
                onEdit(theme);
                onMenuToggle(null);
              }}
              icon={<Edit className="w-3.5 h-3.5" />}
            >
              {t('management.themes.actions.editTheme')}
            </ActionMenuItem>
          )}
          <ActionMenuItem
            onClick={() => {
              onExport(theme);
              onMenuToggle(null);
            }}
            icon={<Download className="w-3.5 h-3.5" />}
          >
            {t('management.themes.actions.export')}
          </ActionMenuItem>
          {!isSystem && isAdmin && (
            <>
              <ActionMenuDivider />
              <ActionMenuDangerItem
                onClick={() => {
                  onDelete(currentMenuId, theme.meta.name);
                  onMenuToggle(null);
                }}
                icon={<Trash2 className="w-3.5 h-3.5" />}
              >
                {t('management.themes.actions.delete')}
              </ActionMenuDangerItem>
            </>
          )}
        </ActionMenu>
      </div>

      {/* Color Preview Strip */}
      <div className="flex gap-1">
        {colorPreview.map((item, idx) => (
          <Tooltip key={idx} content={item.label} position="bottom" className="flex-1">
            <div
              className="h-5 rounded transition-transform hover:scale-y-125"
              style={{
                backgroundColor: item.color,
                border:
                  item.color === '#ffffff' || item.color.toLowerCase().includes('fff')
                    ? '1px solid var(--theme-border-secondary)'
                    : 'none'
              }}
            />
          </Tooltip>
        ))}
      </div>
    </div>
  );
};

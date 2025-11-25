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
  isAuthenticated: boolean;
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
  isAuthenticated,
  themeActionMenu,
  currentMenuId,
  onApplyTheme,
  onPreview,
  onEdit,
  onExport,
  onDelete,
  onMenuToggle
}) => {
  const isMenuOpen = themeActionMenu === currentMenuId;

  const colorPreview = [
    theme.colors.primaryColor || '#3b82f6',
    theme.colors.secondaryColor || '#8b5cf6',
    theme.colors.accentColor || '#06b6d4',
    theme.colors.bgPrimary || '#111827',
    theme.colors.textPrimary || '#ffffff'
  ];

  return (
    <div
      className="rounded-lg p-4 transition-all hover:shadow-md themed-card relative group"
      style={{
        backgroundColor: 'var(--theme-bg-secondary)',
        border: `1px solid ${
          isActive
            ? 'var(--theme-primary)'
            : isPreviewing
              ? 'var(--theme-warning)'
              : 'var(--theme-border-secondary)'
        }`
      }}
    >
      {/* Status Badge - Top Right */}
      {(isActive || isPreviewing) && (
        <div
          className="absolute -top-2 -right-2 px-2 py-0.5 text-xs font-medium rounded-full"
          style={{
            backgroundColor: isActive ? 'var(--theme-primary)' : 'var(--theme-warning)',
            color: 'white'
          }}
        >
          {isActive ? 'Active' : 'Preview'}
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
            {isSystem && (
              <Lock className="w-3 h-3 text-themed-muted flex-shrink-0" />
            )}
          </div>

          {/* Badges Row */}
          <div className="flex flex-wrap gap-1 mb-2">
            {theme.meta.isCommunityTheme && (
              <span
                className="inline-flex items-center gap-1 px-1.5 py-0.5 text-xs rounded"
                style={{
                  backgroundColor: 'var(--theme-info-bg)',
                  color: 'var(--theme-info)'
                }}
              >
                <Globe className="w-2.5 h-2.5" />
                Community
              </span>
            )}
            {theme.meta.basedOn && (
              <span
                className="px-1.5 py-0.5 text-xs rounded"
                style={{
                  backgroundColor: 'var(--theme-accent)',
                  color: 'white'
                }}
              >
                Custom
              </span>
            )}
          </div>

          {theme.meta.basedOn && (
            <p className="text-xs text-themed-muted mb-1">Based on: {theme.meta.basedOn}</p>
          )}
          {theme.meta.description && (
            <p className="text-xs text-themed-muted line-clamp-2 mb-1">{theme.meta.description}</p>
          )}
          <div className="flex items-center gap-2 text-xs text-themed-muted">
            {theme.meta.author && <span>by {theme.meta.author}</span>}
            {theme.meta.version && (
              <span
                className="px-1.5 py-0.5 rounded"
                style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
              >
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
              className="p-1.5 rounded-lg hover:bg-themed-hover transition-colors opacity-0 group-hover:opacity-100"
              style={{ backgroundColor: isMenuOpen ? 'var(--theme-bg-hover)' : 'transparent' }}
            >
              <MoreVertical className="w-4 h-4 text-themed-muted" />
            </button>
          }
        >
          {!isActive && (
            <ActionMenuItem
              onClick={() => {
                onApplyTheme(currentMenuId);
                onMenuToggle(null);
              }}
              icon={<Check className="w-3.5 h-3.5" />}
            >
              Apply Theme
            </ActionMenuItem>
          )}
          <ActionMenuItem
            onClick={() => {
              onPreview(currentMenuId);
              onMenuToggle(null);
            }}
            icon={isPreviewing ? <EyeOff className="w-3.5 h-3.5" /> : <Eye className="w-3.5 h-3.5" />}
          >
            {isPreviewing ? 'Stop Preview' : 'Preview'}
          </ActionMenuItem>
          {!isSystem && isAuthenticated && (
            <ActionMenuItem
              onClick={() => {
                onEdit(theme);
                onMenuToggle(null);
              }}
              icon={<Edit className="w-3.5 h-3.5" />}
            >
              Edit Theme
            </ActionMenuItem>
          )}
          <ActionMenuItem
            onClick={() => {
              onExport(theme);
              onMenuToggle(null);
            }}
            icon={<Download className="w-3.5 h-3.5" />}
          >
            Export
          </ActionMenuItem>
          {!isSystem && isAuthenticated && (
            <>
              <ActionMenuDivider />
              <ActionMenuDangerItem
                onClick={() => {
                  onDelete(currentMenuId, theme.meta.name);
                  onMenuToggle(null);
                }}
                icon={<Trash2 className="w-3.5 h-3.5" />}
              >
                Delete
              </ActionMenuDangerItem>
            </>
          )}
        </ActionMenu>
      </div>

      {/* Color Preview Strip */}
      <div className="flex gap-1">
        {colorPreview.map((color, idx) => (
          <Tooltip
            key={idx}
            content={['Primary', 'Secondary', 'Accent', 'Background', 'Text'][idx]}
            position="bottom"
            className="flex-1"
          >
            <div
              className="h-5 rounded transition-transform hover:scale-y-125"
              style={{
                backgroundColor: color,
                border:
                  color === '#ffffff' || color.toLowerCase().includes('fff')
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

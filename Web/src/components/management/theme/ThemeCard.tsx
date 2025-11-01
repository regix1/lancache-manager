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
import { Theme } from './types';
import { ActionMenu, ActionMenuItem, ActionMenuDivider, ActionMenuDangerItem } from '../../ui/ActionMenu';

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

  return (
    <div
      className="rounded-lg p-4 transition-all hover:shadow-lg themed-card relative"
      style={{
        border: `2px solid ${isActive ? 'var(--theme-primary)' :
                    isPreviewing ? 'var(--theme-warning)' :
                    'var(--theme-border-primary)'}`
      }}
    >
      {/* Theme Header */}
      <div className="flex items-start justify-between mb-3">
        <div className="flex-1">
          <div className="flex items-center gap-2 mb-1">
            <span className="font-semibold text-themed-primary">{theme.meta.name}</span>
            {theme.meta.isDark ? (
              <Moon className="w-3 h-3 text-themed-muted" />
            ) : (
              <Sun className="w-3 h-3 text-themed-warning" />
            )}
            {isActive && (
              <span className="px-2 py-0.5 text-xs rounded themed-button-primary">
                Active
              </span>
            )}
            {isPreviewing && (
              <span className="px-2 py-0.5 text-xs rounded bg-themed-warning text-themed-primary">
                Preview
              </span>
            )}
            {isSystem && (
              <Lock className="w-3 h-3 text-themed-muted" />
            )}
            {theme.meta.isCommunityTheme && (
              <span className="px-2 py-0.5 text-xs rounded bg-themed-info text-white flex items-center gap-1">
                <Globe className="w-3 h-3" />
                Community
              </span>
            )}
            {theme.meta.basedOn && (
              <span className="px-2 py-0.5 text-xs rounded bg-themed-accent text-white">
                Custom
              </span>
            )}
          </div>
          {theme.meta.basedOn && (
            <p className="text-xs text-themed-muted mb-1">
              Based on: {theme.meta.basedOn}
            </p>
          )}
          {theme.meta.description && (
            <p className="text-xs text-themed-muted mb-1">{theme.meta.description}</p>
          )}
          <div className="flex items-center gap-3 text-xs text-themed-muted">
            {theme.meta.author && <span>by {theme.meta.author}</span>}
            {theme.meta.version && <span>v{theme.meta.version}</span>}
          </div>
        </div>

        {/* Action Menu Button */}
        <ActionMenu
          isOpen={isMenuOpen}
          onClose={() => onMenuToggle(null)}
          trigger={
            <button
              onClick={() => onMenuToggle(themeActionMenu === currentMenuId ? null : currentMenuId)}
              className="p-1 rounded hover:bg-themed-hover transition-colors"
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
              icon={<Check className="w-3 h-3" />}
            >
              Apply Theme
            </ActionMenuItem>
          )}
          <ActionMenuItem
            onClick={() => {
              onPreview(currentMenuId);
              onMenuToggle(null);
            }}
            icon={isPreviewing ? <EyeOff className="w-3 h-3" /> : <Eye className="w-3 h-3" />}
          >
            {isPreviewing ? 'Stop Preview' : 'Preview'}
          </ActionMenuItem>
          {!isSystem && isAuthenticated && (
            <ActionMenuItem
              onClick={() => {
                onEdit(theme);
                onMenuToggle(null);
              }}
              icon={<Edit className="w-3 h-3" />}
            >
              Edit Theme
            </ActionMenuItem>
          )}
          <ActionMenuItem
            onClick={() => {
              onExport(theme);
              onMenuToggle(null);
            }}
            icon={<Download className="w-3 h-3" />}
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
                icon={<Trash2 className="w-3 h-3" />}
              >
                Delete
              </ActionMenuDangerItem>
            </>
          )}
        </ActionMenu>
      </div>

      {/* Color Preview Strip */}
      <div className="flex gap-1 mt-3">
        <div
          className="flex-1 h-6 rounded"
          style={{ backgroundColor: theme.colors.primaryColor || '#3b82f6' }}
          title="Primary"
        />
        <div
          className="flex-1 h-6 rounded"
          style={{ backgroundColor: theme.colors.secondaryColor || '#8b5cf6' }}
          title="Secondary"
        />
        <div
          className="flex-1 h-6 rounded"
          style={{ backgroundColor: theme.colors.accentColor || '#06b6d4' }}
          title="Accent"
        />
        <div
          className="flex-1 h-6 rounded"
          style={{ backgroundColor: theme.colors.bgPrimary || '#111827' }}
          title="Background"
        />
        <div
          className="flex-1 h-6 rounded"
          style={{
            border: '1px solid var(--theme-border)',
            backgroundColor: theme.colors.textPrimary || '#ffffff'
          }}
          title="Text"
        />
      </div>
    </div>
  );
};

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
  Trash2
} from 'lucide-react';
import { Theme } from './types';

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
          </div>
          {theme.meta.description && (
            <p className="text-xs text-themed-muted mb-1">{theme.meta.description}</p>
          )}
          <div className="flex items-center gap-3 text-xs text-themed-muted">
            {theme.meta.author && <span>by {theme.meta.author}</span>}
            {theme.meta.version && <span>v{theme.meta.version}</span>}
          </div>
        </div>

        {/* Action Menu Button */}
        <div className="relative">
          <button
            onClick={() => onMenuToggle(themeActionMenu === currentMenuId ? null : currentMenuId)}
            className="p-1 rounded hover:bg-themed-hover transition-colors"
          >
            <MoreVertical className="w-4 h-4 text-themed-muted" />
          </button>

          {/* Dropdown Menu */}
          {themeActionMenu === currentMenuId && (
            <div className="absolute right-0 mt-1 w-40 bg-themed-secondary rounded-lg shadow-lg z-10" style={{
              border: '1px solid var(--theme-border-primary)'
            }}>
              {!isActive && (
                <button
                  onClick={() => {
                    onApplyTheme(currentMenuId);
                    onMenuToggle(null);
                  }}
                  className="w-full px-3 py-2 text-left text-sm hover:bg-themed-hover flex items-center gap-2"
                >
                  <Check className="w-3 h-3" />
                  Apply Theme
                </button>
              )}
              <button
                onClick={() => {
                  onPreview(currentMenuId);
                  onMenuToggle(null);
                }}
                className="w-full px-3 py-2 text-left text-sm hover:bg-themed-hover flex items-center gap-2"
              >
                {isPreviewing ? <EyeOff className="w-3 h-3" /> : <Eye className="w-3 h-3" />}
                {isPreviewing ? 'Stop Preview' : 'Preview'}
              </button>
              {!isSystem && isAuthenticated && (
                <button
                  onClick={() => {
                    onEdit(theme);
                    onMenuToggle(null);
                  }}
                  className="w-full px-3 py-2 text-left text-sm hover:bg-themed-hover flex items-center gap-2"
                >
                  <Edit className="w-3 h-3" />
                  Edit Theme
                </button>
              )}
              <button
                onClick={() => {
                  onExport(theme);
                  onMenuToggle(null);
                }}
                className="w-full px-3 py-2 text-left text-sm hover:bg-themed-hover flex items-center gap-2"
              >
                <Download className="w-3 h-3" />
                Export
              </button>
              {!isSystem && isAuthenticated && (
                <>
                  <div className="border-t my-1" style={{ borderColor: 'var(--theme-border-primary)' }} />
                  <button
                    onClick={() => {
                      onDelete(currentMenuId, theme.meta.name);
                      onMenuToggle(null);
                    }}
                    className="w-full px-3 py-2 text-left text-sm flex items-center gap-2 transition-colors"
                    style={{
                      color: 'var(--theme-error-text)',
                      backgroundColor: 'transparent'
                    }}
                    onMouseEnter={(e) => {
                      e.currentTarget.style.backgroundColor = 'var(--theme-error-bg)';
                    }}
                    onMouseLeave={(e) => {
                      e.currentTarget.style.backgroundColor = 'transparent';
                    }}
                  >
                    <Trash2 className="w-3 h-3" />
                    Delete
                  </button>
                </>
              )}
            </div>
          )}
        </div>
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

import React, { useState, useEffect, useRef } from 'react';
import {
  Download,
  Globe,
  Check,
  Moon,
  Sun,
  ExternalLink,
  Eye,
  EyeOff,
  Loader2,
  RefreshCw
} from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Tooltip } from '@components/ui/Tooltip';
import themeService from '@services/theme.service';
import authService from '@services/auth.service';
import { API_BASE } from '@utils/constants';

interface ColorPreview {
  primaryColor?: string;
  secondaryColor?: string;
  accentColor?: string;
  bgPrimary?: string;
  textPrimary?: string;
  [key: string]: string | undefined;
}

interface GitHubFile {
  name: string;
  type: 'file' | 'dir';
  path: string;
  sha: string;
  size: number;
  download_url: string;
}

interface CommunityTheme {
  name: string;
  fileName: string;
  content: string;
  meta?: {
    id: string;
    name: string;
    description?: string;
    author?: string;
    version?: string;
    isDark?: boolean;
  };
  colors?: ColorPreview;
}

interface CommunityThemeImporterProps {
  isAuthenticated: boolean;
  onThemeImported?: () => void;
  installedThemes?: { meta: { id: string; version?: string } }[];
  autoCheckUpdates?: boolean;
}

const GITHUB_API_BASE =
  'https://api.github.com/repos/regix1/lancache-manager/contents/community-themes';
const GITHUB_RAW_BASE =
  'https://raw.githubusercontent.com/regix1/lancache-manager/refs/heads/main/community-themes';

export const CommunityThemeImporter: React.FC<CommunityThemeImporterProps> = ({
  isAuthenticated,
  onThemeImported,
  installedThemes = [],
  autoCheckUpdates = true
}) => {
  const [communityThemes, setCommunityThemes] = useState<CommunityTheme[]>([]);
  const [loading, setLoading] = useState(false);
  const [importing, setImporting] = useState<string | null>(null);
  const [importedThemes, setImportedThemes] = useState<Set<string>>(new Set());
  const [showImported, setShowImported] = useState(false);
  const [updatingThemes, setUpdatingThemes] = useState<Set<string>>(new Set());
  const loadingInProgressRef = useRef(false);
  const importingThemeRef = useRef<string | null>(null);

  // Helper to show toast notifications
  const showToast = (type: 'success' | 'error' | 'info', message: string) => {
    window.dispatchEvent(new CustomEvent('show-toast', {
      detail: { type, message, duration: 4000 }
    }));
  };

  useEffect(() => {
    loadCommunityThemesAndCheckUpdates();
  }, []);

  // Watch for changes in both community themes and installed themes, and auto-update when both are ready
  useEffect(() => {
    if (
      communityThemes.length > 0 &&
      installedThemes.length > 0 &&
      autoCheckUpdates &&
      isAuthenticated
    ) {
      checkAndUpdateThemes(communityThemes);
    }
  }, [communityThemes, installedThemes, autoCheckUpdates, isAuthenticated]);

  // Check which community themes are already installed
  const isThemeInstalled = (themeId: string): boolean => {
    return installedThemes.some((t) => t.meta.id === themeId);
  };

  const loadCommunityThemes = async () => {
    // Prevent double-clicks
    if (loadingInProgressRef.current) {
      return;
    }
    loadingInProgressRef.current = true;

    setLoading(true);

    try {
      // Fetch the list of files from GitHub
      const response = await fetch(GITHUB_API_BASE);
      if (!response.ok) {
        throw new Error('Failed to fetch community themes from GitHub');
      }

      const files = await response.json();

      // Filter for .toml files
      const tomlFiles = files.filter(
        (file: GitHubFile) => file.name.endsWith('.toml') && file.type === 'file'
      );

      // Fetch content for each theme file
      const themes: CommunityTheme[] = [];
      for (const file of tomlFiles) {
        try {
          const contentResponse = await fetch(`${GITHUB_RAW_BASE}/${file.name}`);
          if (contentResponse.ok) {
            const content = await contentResponse.text();
            const parsedTheme = themeService.parseTomlTheme(content);

            if (parsedTheme) {
              themes.push({
                name: file.name.replace('.toml', ''),
                fileName: file.name,
                content,
                meta: parsedTheme.meta,
                colors: parsedTheme.colors as ColorPreview
              });
            }
          }
        } catch (err) {
          console.error(`Failed to load theme ${file.name}:`, err);
        }
      }

      setCommunityThemes(themes);

      // Automatically check and update themes if enabled
      if (autoCheckUpdates && isAuthenticated) {
        await checkAndUpdateThemes(themes);
      }
    } catch (err: unknown) {
      showToast('error', (err instanceof Error ? err.message : String(err)) || 'Failed to load community themes');
      console.error('Error loading community themes:', err);
    } finally {
      setLoading(false);
      loadingInProgressRef.current = false;
    }
  };

  const handleImportTheme = async (theme: CommunityTheme) => {
    // Prevent double-clicks on the same theme
    if (importingThemeRef.current === theme.fileName) {
      return;
    }

    if (!isAuthenticated) {
      showToast('error', 'Authentication required to import themes');
      return;
    }

    importingThemeRef.current = theme.fileName;
    setImporting(theme.fileName);

    try {
      // Add isCommunityTheme flag to the TOML content
      let modifiedContent = theme.content;

      // Insert isCommunityTheme = true after the isDark line (or at end of meta section)
      if (modifiedContent.includes('[meta]')) {
        // Try to insert after isDark line if it exists
        if (modifiedContent.match(/isDark\s*=\s*(true|false)/)) {
          modifiedContent = modifiedContent.replace(
            /(isDark\s*=\s*(?:true|false))/,
            '$1\nisCommunityTheme = true'
          );
        } else {
          // Otherwise insert before the [colors] section or next section
          modifiedContent = modifiedContent.replace(
            /(\n)(\[(?!meta))/,
            '\nisCommunityTheme = true\n$2'
          );
        }
      }

      // Create a File object from the modified theme content
      const blob = new Blob([modifiedContent], { type: 'application/toml' });
      const file = new File([blob], theme.fileName, { type: 'application/toml' });

      // Upload using the existing theme upload endpoint
      const formData = new FormData();
      formData.append('file', file);

      const response = await fetch(`${API_BASE}/themes/upload`, {
        method: 'POST',
        headers: authService.getAuthHeaders(),
        body: formData
      });

      if (!response.ok) {
        const error = await response.json();
        throw new Error(error.error || 'Failed to import theme');
      }

      // Mark theme as imported
      setImportedThemes((prev) => new Set([...prev, theme.fileName]));
      showToast('success', `Successfully imported "${theme.meta?.name || theme.name}"!`);

      // Call the callback to refresh the theme list
      if (onThemeImported) {
        onThemeImported();
      }
    } catch (err: unknown) {
      showToast('error', (err instanceof Error ? err.message : String(err)) || 'Failed to import theme');
    } finally {
      setImporting(null);
      importingThemeRef.current = null;
    }
  };

  const compareVersions = (v1: string, v2: string): number => {
    const parts1 = v1.split('.').map(Number);
    const parts2 = v2.split('.').map(Number);

    for (let i = 0; i < Math.max(parts1.length, parts2.length); i++) {
      const part1 = parts1[i] || 0;
      const part2 = parts2[i] || 0;

      if (part1 > part2) return 1;
      if (part1 < part2) return -1;
    }

    return 0;
  };

  const checkAndUpdateThemes = async (themes: CommunityTheme[]) => {
    if (!isAuthenticated || !autoCheckUpdates) return;

    const themesToUpdate: CommunityTheme[] = [];

    for (const communityTheme of themes) {
      const installedTheme = installedThemes.find((t) => t.meta.id === communityTheme.meta?.id);

      if (installedTheme && communityTheme.meta?.version && installedTheme.meta.version) {
        const comparison = compareVersions(
          communityTheme.meta.version,
          installedTheme.meta.version
        );

        if (comparison > 0) {
          themesToUpdate.push(communityTheme);
        }
      }
    }

    if (themesToUpdate.length > 0) {
      let successCount = 0;
      for (const theme of themesToUpdate) {
        const success = await updateThemeSilently(theme);
        if (success) successCount++;
      }

      if (successCount > 0) {
        showToast('success', `Auto-updated ${successCount} theme${successCount !== 1 ? 's' : ''} to latest version!`);
      }
    }
  };

  const updateThemeSilently = async (theme: CommunityTheme): Promise<boolean> => {
    if (!isAuthenticated) return false;

    setUpdatingThemes((prev) => new Set([...prev, theme.fileName]));

    try {
      // Add isCommunityTheme flag to the TOML content
      let modifiedContent = theme.content;

      // Insert isCommunityTheme = true after the isDark line (or at end of meta section)
      if (modifiedContent.includes('[meta]')) {
        // Try to insert after isDark line if it exists
        if (modifiedContent.match(/isDark\s*=\s*(true|false)/)) {
          modifiedContent = modifiedContent.replace(
            /(isDark\s*=\s*(?:true|false))/,
            '$1\nisCommunityTheme = true'
          );
        } else {
          // Otherwise insert before the [colors] section or next section
          modifiedContent = modifiedContent.replace(
            /(\n)(\[(?!meta))/,
            '\nisCommunityTheme = true\n$2'
          );
        }
      }

      const blob = new Blob([modifiedContent], { type: 'application/toml' });
      const file = new File([blob], theme.fileName, { type: 'application/toml' });

      const formData = new FormData();
      formData.append('file', file);

      const response = await fetch(`${API_BASE}/themes/upload`, {
        method: 'POST',
        headers: authService.getAuthHeaders(),
        body: formData
      });

      if (!response.ok) {
        const error = await response.json();
        throw new Error(error.error || 'Failed to update theme');
      }

      console.log(
        `Auto-updated theme: ${theme.meta?.name || theme.name} to v${theme.meta?.version}`
      );

      if (onThemeImported) {
        onThemeImported();
      }

      return true;
    } catch (err: unknown) {
      console.error(`Failed to auto-update theme ${theme.meta?.name || theme.name}:`, err);
      return false;
    } finally {
      setUpdatingThemes((prev) => {
        const next = new Set(prev);
        next.delete(theme.fileName);
        return next;
      });
    }
  };

  const loadCommunityThemesAndCheckUpdates = async () => {
    await loadCommunityThemes();
  };

  const getColorPreview = (colors: ColorPreview | undefined) => {
    if (!colors) return [];
    return [
      colors.primaryColor,
      colors.secondaryColor,
      colors.accentColor,
      colors.bgPrimary,
      colors.textPrimary
    ].filter(Boolean) as string[];
  };

  // Count themes that would be visible
  const visibleThemesCount = communityThemes.filter((theme) => {
    const isInstalled = isThemeInstalled(theme.meta?.id || '');
    const isImported = importedThemes.has(theme.fileName);
    return showImported || (!isInstalled && !isImported);
  }).length;

  const allImported = communityThemes.length > 0 && visibleThemesCount === 0 && !showImported;

  return (
    <div className="rounded-lg border bg-themed-tertiary border-themed-secondary">
      {/* Header */}
      <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 p-4 border-b border-themed-secondary">
        <div className="flex items-center gap-3">
          <div className="w-8 h-8 rounded-lg flex items-center justify-center icon-bg-purple flex-shrink-0">
            <Globe className="w-4 h-4 icon-purple" />
          </div>
          <div className="min-w-0">
            <h4 className="text-sm font-semibold text-themed-primary">Community Themes</h4>
            <p className="text-xs text-themed-muted">
              {communityThemes.length} available
              {installedThemes.filter(t => communityThemes.some(ct => ct.meta?.id === t.meta.id)).length > 0 && (
                <span> · {installedThemes.filter(t => communityThemes.some(ct => ct.meta?.id === t.meta.id)).length} installed</span>
              )}
            </p>
          </div>
        </div>
        <div className="flex items-center gap-2 flex-shrink-0">
          {communityThemes.length > 0 && (
            <Tooltip content={showImported ? 'Hide imported themes' : 'Show imported themes'} position="bottom">
              <Button
                variant="default"
                size="xs"
                onClick={() => setShowImported(!showImported)}
              >
                {showImported ? <EyeOff className="w-3.5 h-3.5" /> : <Eye className="w-3.5 h-3.5" />}
              </Button>
            </Tooltip>
          )}
          <Tooltip content="Refresh" position="bottom">
            <Button
              variant="default"
              size="xs"
              onClick={loadCommunityThemes}
              disabled={loading}
            >
              {loading ? (
                <Loader2 className="w-3.5 h-3.5 animate-spin" />
              ) : (
                <RefreshCw className="w-3.5 h-3.5" />
              )}
            </Button>
          </Tooltip>
          <a
            href="https://github.com/regix1/lancache-manager/tree/main/community-themes"
            target="_blank"
            rel="noopener noreferrer"
            className="text-xs text-themed-accent hover:text-themed-primary flex items-center gap-1 transition-colors px-2 py-1 rounded hover:bg-themed-hover"
          >
            <ExternalLink className="w-3 h-3" />
            GitHub
          </a>
        </div>
      </div>

      {/* Content */}
      <div className="p-4">
        {/* Auto-Update Progress */}
        {updatingThemes.size > 0 && (
          <div className="flex items-center gap-2 mb-4 p-3 rounded-lg bg-themed-info">
            <Loader2 className="w-4 h-4 animate-spin icon-info" />
            <span className="text-sm text-themed-info">
              Auto-updating {updatingThemes.size} theme{updatingThemes.size !== 1 ? 's' : ''}...
            </span>
          </div>
        )}

        {/* Loading State */}
        {loading && communityThemes.length === 0 && (
          <div className="flex items-center justify-center py-8 text-themed-muted">
            <Loader2 className="w-5 h-5 animate-spin mr-2" />
            <span className="text-sm">Loading community themes...</span>
          </div>
        )}

        {/* Empty State */}
        {!loading && communityThemes.length === 0 && (
          <div className="text-center py-8 text-themed-muted">
            <Globe className="w-10 h-10 mx-auto mb-2 opacity-30" />
            <p className="text-sm">No community themes available</p>
          </div>
        )}

        {/* All Imported State */}
        {allImported && (
          <div className="text-center py-8 text-themed-muted">
            <Check className="w-10 h-10 mx-auto mb-2 opacity-50 icon-green" />
            <p className="text-sm font-medium mb-1">All themes imported!</p>
            <p className="text-xs">Click the eye icon to view installed themes</p>
          </div>
        )}

        {/* Community Themes Grid */}
        {communityThemes.length > 0 && !allImported && (
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-3">
            {communityThemes.map((theme) => {
              const isInstalled = isThemeInstalled(theme.meta?.id || '');
              const isImported = importedThemes.has(theme.fileName);
              const isImporting = importing === theme.fileName;
              const isUpdating = updatingThemes.has(theme.fileName);
              const colorPreview = getColorPreview(theme.colors);
              const shouldHide = !showImported && (isInstalled || isImported);

              if (shouldHide) return null;

              return (
                <div
                  key={theme.fileName}
                  className={`rounded-lg border p-3 transition-all hover:border-themed-primary bg-themed-secondary ${
                    isInstalled || isImported ? 'border-success' : 'border-themed-secondary'
                  }`}
                >
                  {/* Theme Header */}
                  <div className="flex items-start justify-between mb-2">
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-2 mb-1">
                        <span className="font-medium text-themed-primary text-sm truncate">
                          {theme.meta?.name || theme.name}
                        </span>
                        {theme.meta?.isDark ? (
                          <Moon className="w-3 h-3 text-themed-muted flex-shrink-0" />
                        ) : (
                          <Sun className="w-3 h-3 icon-yellow flex-shrink-0" />
                        )}
                      </div>
                      {theme.meta?.description && (
                        <p className="text-xs text-themed-muted line-clamp-2 mb-1">
                          {theme.meta.description}
                        </p>
                      )}
                      <div className="flex items-center gap-2 text-xs text-themed-muted">
                        {theme.meta?.author && <span>by {theme.meta.author}</span>}
                        {theme.meta?.version && (
                          <span className="px-1.5 py-0.5 rounded text-xs bg-themed-tertiary">
                            v{theme.meta.version}
                          </span>
                        )}
                      </div>
                    </div>
                    {(isImported || isInstalled) && !isUpdating && (
                      <div className="flex-shrink-0 w-6 h-6 rounded-full flex items-center justify-center bg-themed-success">
                        <Check className="w-3 h-3 icon-success" />
                      </div>
                    )}
                    {isUpdating && (
                      <div className="flex-shrink-0 w-6 h-6 rounded-full flex items-center justify-center bg-themed-info">
                        <Loader2 className="w-3 h-3 animate-spin icon-info" />
                      </div>
                    )}
                  </div>

                  {/* Color Preview */}
                  <div className="flex gap-1 mb-3">
                    {colorPreview.map((color, idx) => (
                      <Tooltip key={idx} content={color} position="bottom" className="flex-1">
                        <div
                          className="h-5 rounded"
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

                  {/* Import Button */}
                  <Button
                    variant={isImported || isInstalled ? 'default' : 'filled'}
                    color={isImported || isInstalled ? 'default' : 'purple'}
                    size="xs"
                    fullWidth
                    onClick={() => handleImportTheme(theme)}
                    disabled={!isAuthenticated || isImporting || isImported || isInstalled}
                    loading={isImporting}
                  >
                    {isImported || isInstalled ? (
                      <>
                        <Check className="w-3 h-3 mr-1" />
                        Installed
                      </>
                    ) : (
                      <>
                        <Download className="w-3 h-3 mr-1" />
                        Import
                      </>
                    )}
                  </Button>
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
};

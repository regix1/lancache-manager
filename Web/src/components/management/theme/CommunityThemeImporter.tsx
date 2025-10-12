import React, { useState, useEffect } from 'react';
import {
  Download,
  RefreshCw,
  Globe,
  Check,
  AlertCircle,
  Moon,
  Sun,
  ExternalLink,
  Eye,
  EyeOff
} from 'lucide-react';
import { Button } from '../../ui/Button';
import { Alert } from '../../ui/Alert';
import themeService from '../../../services/theme.service';
import authService from '../../../services/auth.service';
import { API_BASE } from '../../../utils/constants';

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
  colors?: any;
}

interface CommunityThemeImporterProps {
  isAuthenticated: boolean;
  onThemeImported?: () => void;
  installedThemes?: Array<{ meta: { id: string } }>;
}

const GITHUB_API_BASE = 'https://api.github.com/repos/regix1/lancache-manager/contents/community-themes';
const GITHUB_RAW_BASE = 'https://raw.githubusercontent.com/regix1/lancache-manager/refs/heads/main/community-themes';

export const CommunityThemeImporter: React.FC<CommunityThemeImporterProps> = ({
  isAuthenticated,
  onThemeImported,
  installedThemes = []
}) => {
  const [communityThemes, setCommunityThemes] = useState<CommunityTheme[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [importing, setImporting] = useState<string | null>(null);
  const [importedThemes, setImportedThemes] = useState<Set<string>>(new Set());
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [showImported, setShowImported] = useState(false);

  useEffect(() => {
    loadCommunityThemes();
  }, []);

  // Check which community themes are already installed
  const isThemeInstalled = (themeId: string): boolean => {
    return installedThemes.some(t => t.meta.id === themeId);
  };

  const loadCommunityThemes = async () => {
    setLoading(true);
    setError(null);

    try {
      // Fetch the list of files from GitHub
      const response = await fetch(GITHUB_API_BASE);
      if (!response.ok) {
        throw new Error('Failed to fetch community themes from GitHub');
      }

      const files = await response.json();

      // Filter for .toml files
      const tomlFiles = files.filter((file: any) =>
        file.name.endsWith('.toml') && file.type === 'file'
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
                colors: parsedTheme.colors
              });
            }
          }
        } catch (err) {
          console.error(`Failed to load theme ${file.name}:`, err);
        }
      }

      setCommunityThemes(themes);
    } catch (err: any) {
      setError(err.message || 'Failed to load community themes');
      console.error('Error loading community themes:', err);
    } finally {
      setLoading(false);
    }
  };

  const handleImportTheme = async (theme: CommunityTheme) => {
    if (!isAuthenticated) {
      setError('Authentication required to import themes');
      return;
    }

    setImporting(theme.fileName);
    setError(null);
    setSuccessMessage(null);

    try {
      // Create a File object from the theme content
      const blob = new Blob([theme.content], { type: 'application/toml' });
      const file = new File([blob], theme.fileName, { type: 'application/toml' });

      // Upload using the existing theme upload endpoint
      const formData = new FormData();
      formData.append('file', file);

      const response = await fetch(`${API_BASE}/theme/upload`, {
        method: 'POST',
        headers: authService.getAuthHeaders(),
        body: formData
      });

      if (!response.ok) {
        const error = await response.json();
        throw new Error(error.error || 'Failed to import theme');
      }

      // Mark theme as imported
      setImportedThemes(prev => new Set([...prev, theme.fileName]));
      setSuccessMessage(`Successfully imported "${theme.meta?.name || theme.name}"!`);

      // Call the callback to refresh the theme list
      if (onThemeImported) {
        onThemeImported();
      }

      // Clear success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err: any) {
      setError(err.message || 'Failed to import theme');
    } finally {
      setImporting(null);
    }
  };

  const getColorPreview = (colors: any) => {
    return [
      colors.primaryColor || '#3b82f6',
      colors.secondaryColor || '#8b5cf6',
      colors.accentColor || '#06b6d4',
      colors.bgPrimary || '#111827',
      colors.textPrimary || '#ffffff'
    ];
  };

  return (
    <div className="space-y-4">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <Globe className="w-5 h-5 icon-purple" />
          <h4 className="text-sm font-semibold text-themed-primary">Community Themes</h4>
        </div>
        <div className="flex items-center gap-2">
          {communityThemes.length > 0 && (
            <Button
              variant="filled"
              color="default"
              size="sm"
              onClick={() => setShowImported(!showImported)}
              leftSection={showImported ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
              className="transition-all duration-200"
            >
              {showImported ? 'Hide Imported' : 'Show Imported'}
            </Button>
          )}
          <a
            href="https://github.com/regix1/lancache-manager/tree/main/community-themes"
            target="_blank"
            rel="noopener noreferrer"
            className="text-xs text-themed-accent hover:text-themed-primary flex items-center gap-1 transition-colors duration-200"
          >
            <ExternalLink className="w-3 h-3" />
            View on GitHub
          </a>
          <button
            onClick={loadCommunityThemes}
            disabled={loading}
            className="p-2 rounded-lg transition-colors duration-200 hover:bg-themed-hover"
            title="Refresh community themes"
          >
            <RefreshCw className={`w-4 h-4 text-themed-muted ${loading ? 'animate-spin' : ''}`} />
          </button>
        </div>
      </div>

      {/* Error Alert */}
      {error && (
        <Alert color="red" className="flex items-center gap-2">
          <AlertCircle className="w-4 h-4" />
          {error}
        </Alert>
      )}

      {/* Success Alert */}
      {successMessage && (
        <Alert color="green" className="flex items-center gap-2">
          <Check className="w-4 h-4" />
          {successMessage}
        </Alert>
      )}

      {/* Authentication Warning */}
      {!isAuthenticated && (
        <Alert color="yellow">
          Authentication required to import community themes
        </Alert>
      )}

      {/* Loading State */}
      {loading && communityThemes.length === 0 && (
        <div className="text-center py-8 text-themed-muted">
          <RefreshCw className="w-8 h-8 animate-spin mx-auto mb-2" />
          <p>Loading community themes...</p>
        </div>
      )}

      {/* Community Themes Grid */}
      {!loading && communityThemes.length === 0 && !error && (
        <div className="text-center py-8 text-themed-muted">
          <Globe className="w-12 h-12 mx-auto mb-2 opacity-50" />
          <p>No community themes available</p>
        </div>
      )}

      {communityThemes.length > 0 && (
        <>
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
            {communityThemes.map((theme) => {
                const isInstalled = isThemeInstalled(theme.meta?.id || '');
                const isImported = importedThemes.has(theme.fileName);
                const isImporting = importing === theme.fileName;
                const colorPreview = getColorPreview(theme.colors);
                const shouldHide = !showImported && (isInstalled || isImported);

            return (
              <div
                key={theme.fileName}
                className={`rounded-lg themed-card border transition-all duration-300 ${
                  shouldHide
                    ? 'opacity-0 scale-95 h-0 p-0 m-0 border-0 overflow-hidden pointer-events-none'
                    : 'opacity-100 scale-100 p-4 border-themed-secondary hover:border-themed-primary'
                }`}
                style={{
                  transition: 'opacity 0.3s ease-out, transform 0.3s ease-out, height 0.3s ease-out, padding 0.3s ease-out, margin 0.3s ease-out, border-width 0.3s ease-out'
                }}
              >
                {/* Theme Header */}
                <div className="mb-3">
                  <div className="flex items-center justify-between mb-1">
                    <div className="flex items-center gap-2">
                      <span className="font-semibold text-themed-primary">
                        {theme.meta?.name || theme.name}
                      </span>
                      {theme.meta?.isDark ? (
                        <Moon className="w-3 h-3 text-themed-muted" />
                      ) : (
                        <Sun className="w-3 h-3 text-themed-warning" />
                      )}
                      {(isImported || isInstalled) && (
                        <span className="px-2 py-0.5 text-xs rounded bg-themed-success text-white">
                          Imported
                        </span>
                      )}
                    </div>
                  </div>

                  {theme.meta?.description && (
                    <p className="text-xs text-themed-muted mb-2">
                      {theme.meta.description}
                    </p>
                  )}

                  <div className="flex items-center gap-3 text-xs text-themed-muted">
                    {theme.meta?.author && <span>by {theme.meta.author}</span>}
                    {theme.meta?.version && <span>v{theme.meta.version}</span>}
                  </div>
                </div>

                {/* Color Preview */}
                <div className="mb-3">
                  <p className="text-xs text-themed-muted mb-1">Color Preview:</p>
                  <div className="flex gap-1">
                    {colorPreview.map((color, idx) => (
                      <div
                        key={idx}
                        className="flex-1 h-6 rounded"
                        style={{
                          backgroundColor: color,
                          border: color === '#ffffff' || color.toLowerCase().includes('fff')
                            ? '1px solid var(--theme-border-secondary)'
                            : 'none'
                        }}
                        title={color}
                      />
                    ))}
                  </div>
                </div>

                {/* Import Button */}
                <Button
                  variant="filled"
                  color="purple"
                  size="sm"
                  fullWidth
                  leftSection={(isImported || isInstalled) ? <Check className="w-4 h-4" /> : <Download className="w-4 h-4" />}
                  onClick={() => handleImportTheme(theme)}
                  disabled={!isAuthenticated || isImporting || isImported || isInstalled}
                  loading={isImporting}
                >
                  {(isImported || isInstalled) ? 'Imported' : 'Import Theme'}
                </Button>
              </div>
            );
          })}
          </div>

          {/* Empty State when all themes are hidden */}
          {!showImported && communityThemes.every(theme => {
            const isInstalled = isThemeInstalled(theme.meta?.id || '');
            const isImported = importedThemes.has(theme.fileName);
            return isInstalled || isImported;
          }) && (
            <div className="text-center py-8 text-themed-muted animate-fadeIn">
              <Check className="w-12 h-12 mx-auto mb-2 opacity-50" />
              <p className="font-medium mb-1">All themes imported!</p>
              <p className="text-sm">Click "Show Imported" to view installed themes</p>
            </div>
          )}
        </>
      )}
    </div>
  );
};

import React, { useState, useEffect, useRef, useCallback } from 'react';
import {
  Palette,
  Download,
  RefreshCw,
  Lock,
  Plus,
  Sparkles,
  Layers,
  Brush,
  Edit,
  Loader2,
  FileText,
  Settings2,
  Moon,
  Sun
} from 'lucide-react';
import { useTranslation } from 'react-i18next';
import themeService from '@services/theme.service';
import preferencesService from '@services/preferences.service';
import authService from '@services/auth.service';
import { useSessionPreferences } from '@contexts/SessionPreferencesContext';
import ApiService from '@services/api.service';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { HelpPopover, HelpSection, HelpNote } from '@components/ui/HelpPopover';
import { API_BASE } from '@utils/constants';
import { Tooltip } from '@components/ui/Tooltip';
import { ThemeCard } from './ThemeCard';
import CreateThemeModal from '@components/modals/theme/CreateThemeModal';
import EditThemeModal from '@components/modals/theme/EditThemeModal';
import { DeleteConfirmModal } from '@components/modals/theme/DeleteConfirmModal';
import { CommunityThemeImporter } from './CommunityThemeImporter';
import { colorGroups } from './constants';
import { type Theme, type ThemeManagerProps, type EditableTheme, type ThemeColors } from './types';
import { useNotifications } from '@contexts/notifications';

const ThemeManager: React.FC<ThemeManagerProps> = ({ isAuthenticated }) => {
  const { t } = useTranslation();
  const { addNotification } = useNotifications();

  // State Management
  const [themes, setThemes] = useState<Theme[]>([]);
  const [currentTheme, setCurrentTheme] = useState('dark-default');
  const [loading, setLoading] = useState(false);
  const [dragActive, setDragActive] = useState(false);
  const [previewTheme, setPreviewTheme] = useState<string | null>(null);
  const [createModalOpen, setCreateModalOpen] = useState(false);
  const [editModalOpen, setEditModalOpen] = useState(false);
  const [themePendingDeletion, setThemePendingDeletion] = useState<{
    id: string;
    name: string;
  } | null>(null);
  const [showCleanupModal, setShowCleanupModal] = useState(false);
  const [editingTheme, setEditingTheme] = useState<Theme | null>(null);
  const [expandedGroups, setExpandedGroups] = useState<string[]>(['foundation']);
  const [activeTab, setActiveTab] = useState<'themes' | 'customize'>('themes');
  const [themeActionMenu, setThemeActionMenu] = useState<string | null>(null);
  const [copiedColor, setCopiedColor] = useState<string | null>(null);
  const [organizationMode, setOrganizationMode] = useState<'category' | 'page'>('category');
  const [selectedPage, setSelectedPage] = useState<string>('all');
  const [editOrganizationMode, setEditOrganizationMode] = useState<'category' | 'page'>('category');
  const [editSelectedPage, setEditSelectedPage] = useState<string>('all');

  const [editedTheme, setEditedTheme] = useState<EditableTheme>({
    name: '',
    description: '',
    author: '',
    version: '1.0.0',
    isDark: true,
    customCSS: ''
  });
  const [newTheme, setNewTheme] = useState<EditableTheme>({
    name: '',
    description: '',
    author: '',
    version: '1.0.0',
    isDark: true,
    customCSS: ''
  });

  const fileInputRef = useRef<HTMLInputElement>(null);

  // Get theme preference from SessionPreferencesContext
  const { currentPreferences } = useSessionPreferences();
  
  // Sync currentTheme with context when it changes (handles SignalR updates)
  useEffect(() => {
    if (currentPreferences?.selectedTheme && !previewTheme) {
      setCurrentTheme(currentPreferences.selectedTheme);
    }
  }, [currentPreferences?.selectedTheme, previewTheme]);

  // Load themes on mount
  useEffect(() => {
    loadThemes();

    // Load preview state first
    const savedPreview = themeService.getPreviewTheme();
    if (savedPreview) {
      setPreviewTheme(savedPreview);
      // If in preview mode, restore the original theme as the "current" theme
      const originalTheme = themeService.getOriginalThemeBeforePreview();
      if (originalTheme) setCurrentTheme(originalTheme);
    } else {
      // Not in preview mode, use the currently applied theme
      const saved = themeService.getCurrentThemeId();
      if (saved) setCurrentTheme(saved);
    }
  }, []);

  // Handler Functions
  const loadThemes = useCallback(async () => {
    setLoading(true);
    try {
      const data = await themeService.loadThemes();
      setThemes(data);
    } catch (error) {
      console.error('Error loading themes:', error);
    } finally {
      setLoading(false);
    }
  }, []);

  const handleThemeChange = async (themeId: string) => {
    if (authService.authMode === 'guest') {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('management.themes.notifications.guestCannotChange'),
        details: { notificationType: 'error' }
      });
      return;
    }
    try {
      if (isAuthenticated) {
        const saved = await preferencesService.setPreference('selectedTheme', themeId);
        if (!saved) {
          addNotification({
            type: 'generic',
            status: 'failed',
            message: t('management.themes.notifications.failedToSave'),
            details: { notificationType: 'error' }
          });
        }
      }
      await themeService.setTheme(themeId);
      setCurrentTheme(themeId);
      setPreviewTheme(null);
      themeService.clearPreviewTheme();
      themeService.clearOriginalThemeBeforePreview();
      window.location.reload();
    } catch (error) {
      console.error('Failed to change theme:', error);
    }
  };

  const handlePreview = async (themeId: string) => {
    if (authService.authMode === 'guest') {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('management.themes.notifications.guestCannotPreview'),
        details: { notificationType: 'error' }
      });
      return;
    }
    if (previewTheme === themeId) {
      // Toggle off preview - restore original theme from before preview started
      const originalTheme = themeService.getOriginalThemeBeforePreview() || 'dark-default';
      await themeService.setTheme(originalTheme);
      setPreviewTheme(null);
      themeService.clearPreviewTheme();
      themeService.clearOriginalThemeBeforePreview();
      window.location.reload();
    } else {
      // Toggle on preview - save current theme and apply preview theme
      themeService.setOriginalThemeBeforePreview(currentTheme);
      await themeService.setTheme(themeId);
      setPreviewTheme(themeId);
      themeService.setPreviewTheme(themeId);
      window.location.reload();
    }
  };

  const handleEditTheme = (theme: Theme) => {
    setEditingTheme(theme);
    setEditedTheme({
      name: theme.meta.name,
      description: theme.meta.description || '',
      author: theme.meta.author || '',
      version: theme.meta.version || '1.0.0',
      isDark: theme.meta.isDark ?? true,
      ...theme.colors,
      customCSS: theme.css?.content || ''
    });
    setEditModalOpen(true);
  };

  const handleSaveEditedTheme = async () => {
    if (!editingTheme) return;

    setLoading(true);
    try {
      const { name, description, author, version, isDark, customCSS, ...colors } = editedTheme;

      // Check if this is a community theme - if so, create a copy instead of editing
      const isCommunityTheme = editingTheme.meta.isCommunityTheme === true;
      const newThemeId = isCommunityTheme ? `${editingTheme.meta.id}-custom` : editingTheme.meta.id;
      const newThemeName = isCommunityTheme ? `${name} (Custom)` : name;

      const themeData: Theme = {
        meta: {
          id: newThemeId,
          name: newThemeName,
          description,
          author,
          version,
          isDark,
          ...(isCommunityTheme
            ? {
                basedOn: editingTheme.meta.name,
                isCommunityTheme: false // Custom versions are not community themes
              }
            : {})
        },
        colors: colors as ThemeColors,
        css: customCSS ? { content: customCSS } : undefined
      };

      // Convert to TOML and upload
      const toml = themeService.exportTheme(themeData);
      const blob = new Blob([toml], { type: 'application/toml' });
      const file = new File([blob], `${newThemeId}.toml`, { type: 'application/toml' });

      const formData = new FormData();
      formData.append('file', file);

      const response = await fetch(`${API_BASE}/themes/upload`, ApiService.getFetchOptions({
        method: 'POST',
        body: formData
      }));

      if (!response.ok) throw new Error('Failed to update theme');

      await loadThemes();
      setEditModalOpen(false);
      setEditingTheme(null);

      // If we created a copy and the original was active, switch to the copy
      if (
        isCommunityTheme &&
        (currentTheme === editingTheme.meta.id || previewTheme === editingTheme.meta.id)
      ) {
        await themeService.setTheme(newThemeId);
        setCurrentTheme(newThemeId);
        window.location.reload();
      } else if (
        !isCommunityTheme &&
        (currentTheme === editingTheme.meta.id || previewTheme === editingTheme.meta.id)
      ) {
        await themeService.setTheme(editingTheme.meta.id);
        window.location.reload();
      }
    } catch (error) {
      console.error('Error updating theme:', error);
    } finally {
      setLoading(false);
    }
  };

  const handleCreateTheme = async () => {
    setLoading(true);
    try {
      const { name, description, author, version, isDark, customCSS, ...colors } = newTheme;
      const trimmedName = name.trim();
      if (!trimmedName) {
        addNotification({
          type: 'generic',
          status: 'failed',
          message: t('management.themes.notifications.nameRequired'),
          details: { notificationType: 'error' }
        });
        return;
      }

      // Generate a safe ID from the theme name
      const themeId = trimmedName.toLowerCase().replace(/[^a-z0-9-_]/g, '-');

      const themeData: Theme = {
        meta: {
          id: themeId,
          name: trimmedName,
          description,
          author,
          version,
          isDark
        },
        colors: colors as ThemeColors,
        css: customCSS ? { content: customCSS } : undefined
      };

      // Convert to TOML and upload
      const toml = themeService.exportTheme(themeData);
      const blob = new Blob([toml], { type: 'application/toml' });
      const file = new File([blob], `${themeId}.toml`, { type: 'application/toml' });

      const formData = new FormData();
      formData.append('file', file);

      const response = await fetch(`${API_BASE}/themes/upload`, ApiService.getFetchOptions({
        method: 'POST',
        body: formData
      }));

      if (!response.ok) throw new Error('Failed to create theme');

      await loadThemes();
      setCreateModalOpen(false);
      setNewTheme({
        name: '',
        description: '',
        author: '',
        version: '1.0.0',
        isDark: true,
        customCSS: ''
      });
    } catch (error) {
      console.error('Error creating theme:', error);
    } finally {
      setLoading(false);
    }
  };

  const handleDelete = (themeId: string, themeName: string) => {
    setThemePendingDeletion({ id: themeId, name: themeName });
  };

  const confirmDelete = async () => {
    if (!themePendingDeletion) return;

    setLoading(true);
    try {
      const response = await fetch(`${API_BASE}/themes/${themePendingDeletion.id}`, ApiService.getFetchOptions({
        method: 'DELETE'
      }));

      // Handle 404 gracefully - theme might already be deleted
      if (!response.ok && response.status !== 404) {
        throw new Error('Failed to delete theme');
      }

      if (currentTheme === themePendingDeletion.id) {
        await themeService.setTheme('dark-default');
        setCurrentTheme('dark-default');
      }
      if (previewTheme === themePendingDeletion.id) {
        setPreviewTheme(null);
        themeService.clearPreviewTheme();
        themeService.clearOriginalThemeBeforePreview();
      }

      await loadThemes();
      setThemePendingDeletion(null);
    } catch (error) {
      console.error('Error deleting theme:', error);
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('management.themes.notifications.deleteFailed'),
        details: { notificationType: 'error' }
      });
    } finally {
      setLoading(false);
    }
  };

  const cleanupThemes = async () => {
    setShowCleanupModal(true);
  };

  const confirmCleanup = async () => {
    setLoading(true);
    try {
      const customThemes = themes.filter((t) => !isSystemTheme(t.meta.id));

      for (const theme of customThemes) {
        await fetch(`${API_BASE}/themes/${theme.meta.id}`, ApiService.getFetchOptions({
          method: 'DELETE'
        }));
      }

      await themeService.setTheme('dark-default');
      setCurrentTheme('dark-default');
      setPreviewTheme(null);
      themeService.clearPreviewTheme();
      themeService.clearOriginalThemeBeforePreview();
      await loadThemes();
      setShowCleanupModal(false);
      window.location.reload();
    } catch (error) {
      console.error('Error cleaning up themes:', error);
    } finally {
      setLoading(false);
    }
  };

  // File Upload Handlers
  const handleDrag = (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    if (e.type === 'dragenter' || e.type === 'dragover') {
      setDragActive(true);
    } else if (e.type === 'dragleave') {
      setDragActive(false);
    }
  };

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setDragActive(false);
    if (e.dataTransfer.files && e.dataTransfer.files[0]) {
      handleFile(e.dataTransfer.files[0]);
    }
  };

  const handleFile = async (file: File) => {
    if (!file.name.endsWith('.toml')) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('management.themes.notifications.uploadToml'),
        details: { notificationType: 'error' }
      });
      return;
    }

    if (file.size > 1024 * 1024) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('management.themes.notifications.fileTooLarge'),
        details: { notificationType: 'error' }
      });
      return;
    }

    setLoading(true);
    try {
      const formData = new FormData();
      formData.append('file', file);

      const response = await fetch(`${API_BASE}/themes/upload`, ApiService.getFetchOptions({
        method: 'POST',
        body: formData
      }));

      if (!response.ok) {
        const error = await response.json();
        throw new Error(error.error || t('management.themes.errors.uploadFailed'));
      }

      addNotification({
        type: 'generic',
        status: 'completed',
        message: t('management.themes.notifications.uploadSuccess'),
        details: { notificationType: 'success' }
      });
      await loadThemes();
    } catch (error: unknown) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: (error instanceof Error ? error.message : String(error)) || t('management.themes.notifications.uploadFailed'),
        details: { notificationType: 'error' }
      });
    } finally {
      setLoading(false);
    }
  };

  const handleExportTheme = (theme: Theme) => {
    const toml = themeService.exportTheme(theme);
    const blob = new Blob([toml], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${theme.meta.id}.toml`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const downloadSampleTheme = () => {
    const sampleTheme = themes.find((t) => t.meta.id === 'dark-default');
    if (sampleTheme) {
      handleExportTheme(sampleTheme);
    }
  };

  // Utility Functions
  const toggleGroup = (groupName: string) => {
    setExpandedGroups((prev) =>
      prev.includes(groupName) ? prev.filter((g) => g !== groupName) : [...prev, groupName]
    );
  };

  const copyColor = (color: string) => {
    navigator.clipboard.writeText(color);
    setCopiedColor(color);
    setTimeout(() => setCopiedColor(null), 2000);
  };

  const isSystemTheme = (themeId: string) => ['dark-default', 'light-default'].includes(themeId);

  // Open Create Modal with current theme colors as defaults
  const openCreateModal = () => {
    const currentThemeData = themes.find((t) => t.meta.id === currentTheme);
    if (currentThemeData) {
      setNewTheme({
        name: '',
        description: '',
        author: '',
        version: '1.0.0',
        isDark: currentThemeData.meta.isDark ?? true,
        ...currentThemeData.colors,
        customCSS: ''
      });
    }
    setCreateModalOpen(true);
  };

  // Get current theme data for display
  const currentThemeData = themes.find((t) => t.meta.id === (previewTheme || currentTheme));

  // Separate themes by type
  const systemThemes = themes.filter((t) => isSystemTheme(t.meta.id));
  const customThemes = themes.filter((t) => !isSystemTheme(t.meta.id));

  return (
    <>
      <Card>
        {/* Header */}
        <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 mb-6">
          <div className="flex items-center gap-3">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-purple flex-shrink-0">
              <Palette className="w-5 h-5 icon-purple" />
            </div>
            <div className="min-w-0">
              <h3 className="text-lg font-semibold text-themed-primary">{t('management.themes.title')}</h3>
              <p className="text-xs text-themed-muted">{t('management.themes.themesAvailable', { count: themes.length })}</p>
            </div>
            <HelpPopover position="left" width={320}>
              <HelpSection title={t('management.themes.help.themeTypes.title')} variant="subtle">
                <div className="divide-y divide-[var(--theme-text-muted)]">
                  <div className="py-1.5 first:pt-0 last:pb-0">
                    <div className="font-medium text-themed-primary">{t('management.themes.help.themeTypes.system.term')}</div>
                    <div className="mt-0.5">{t('management.themes.help.themeTypes.system.description')}</div>
                  </div>
                  <div className="py-1.5 first:pt-0 last:pb-0">
                    <div className="font-medium text-themed-primary">{t('management.themes.help.themeTypes.custom.term')}</div>
                    <div className="mt-0.5">{t('management.themes.help.themeTypes.custom.description')}</div>
                  </div>
                </div>
              </HelpSection>

              <HelpSection title={t('management.themes.help.previewMode.title')} variant="subtle">
                {t('management.themes.help.previewMode.description')}
              </HelpSection>

              <HelpNote type="info">
                {t('management.themes.help.note')}
              </HelpNote>
            </HelpPopover>
          </div>
          <div className="flex items-center gap-2 flex-shrink-0">
            {isAuthenticated ? (
              <>
                <Tooltip content={t('management.themes.createNewTheme')} position="bottom">
                  <Button
                    variant="default"
                    size="sm"
                    onClick={openCreateModal}
                  >
                    <Plus className="w-4 h-4" />
                  </Button>
                </Tooltip>
                <Tooltip content={t('management.themes.deleteAllCustom')} position="bottom">
                  <Button
                    variant="default"
                    size="sm"
                    onClick={cleanupThemes}
                    disabled={loading || customThemes.length === 0}
                  >
                    <Sparkles className="w-4 h-4" />
                  </Button>
                </Tooltip>
              </>
            ) : (
              <Tooltip content={t('common.authRequired')} position="bottom">
                <Button variant="default" size="sm" disabled>
                  <Lock className="w-4 h-4" />
                </Button>
              </Tooltip>
            )}
            <Tooltip content={t('management.themes.refreshThemes')} position="bottom">
              <Button
                variant="default"
                size="sm"
                onClick={() => loadThemes()}
                disabled={loading}
              >
                {loading ? (
                  <Loader2 className="w-4 h-4 animate-spin" />
                ) : (
                  <RefreshCw className="w-4 h-4" />
                )}
              </Button>
            </Tooltip>
          </div>
        </div>

        {/* Tab Navigation */}
        <div className="flex gap-1 mb-6 p-1 rounded-lg bg-themed-tertiary">
          <button
            onClick={() => setActiveTab('themes')}
            className={`flex-1 flex items-center justify-center gap-2 px-4 py-2 rounded-md text-sm font-medium transition-all ${
              activeTab === 'themes' ? 'text-themed-primary shadow-sm tab-active' : 'text-themed-muted hover:text-themed-secondary tab-inactive'
            }`}
          >
            <Layers className="w-4 h-4" />
            {t('management.themes.tabs.themes')}
          </button>
          <button
            onClick={() => setActiveTab('customize')}
            className={`flex-1 flex items-center justify-center gap-2 px-4 py-2 rounded-md text-sm font-medium transition-all ${
              activeTab === 'customize' ? 'text-themed-primary shadow-sm tab-active' : 'text-themed-muted hover:text-themed-secondary tab-inactive'
            }`}
          >
            <Brush className="w-4 h-4" />
            {t('management.themes.tabs.customize')}
          </button>
        </div>

        {activeTab === 'themes' ? (
          <div className="space-y-6">
            {/* Guest User Alert */}
            {authService.authMode === 'guest' && (
              <Alert color="blue">
                <div>
                  <p className="text-sm font-medium mb-1">{t('management.themes.guestMode.title')}</p>
                  <p className="text-sm">
                    {t('management.themes.guestMode.description')}
                  </p>
                </div>
              </Alert>
            )}

            {/* Current Theme Section */}
            <div className="p-4 rounded-lg border bg-themed-tertiary border-themed-secondary">
              <div className="flex items-center gap-2 mb-4">
                <Settings2 className="w-4 h-4 text-themed-accent" />
                <span className="text-sm font-medium text-themed-primary">{t('management.themes.activeTheme')}</span>
              </div>

              <div className="flex flex-col sm:flex-row sm:items-center gap-3 sm:gap-4">
                <div className="flex-1 min-w-0">
                  <EnhancedDropdown
                    options={themes.map((theme) => ({
                      value: theme.meta.id,
                      label: `${theme.meta.name}${isSystemTheme(theme.meta.id) ? ` (${t('management.themes.systemBadge')})` : ''}${previewTheme === theme.meta.id ? ` (${t('management.themes.previewBadge')})` : ''}`
                    }))}
                    value={previewTheme || currentTheme}
                    onChange={handleThemeChange}
                    placeholder={t('management.themes.selectTheme')}
                    className="w-full"
                    disabled={authService.authMode === 'guest'}
                  />
                </div>
                {currentThemeData && (
                  <div className="flex items-center gap-2 flex-shrink-0">
                    {currentThemeData.meta.isDark ? (
                      <Moon className="w-4 h-4 text-themed-muted" />
                    ) : (
                      <Sun className="w-4 h-4 icon-yellow" />
                    )}
                    <div className="flex gap-0.5">
                      {[
                        currentThemeData.colors.primaryColor,
                        currentThemeData.colors.secondaryColor,
                        currentThemeData.colors.accentColor
                      ].filter(Boolean).map((color, i) => (
                        <div
                          key={i}
                          className="w-5 h-5 rounded"
                          style={{ backgroundColor: color }}
                        />
                      ))}
                    </div>
                  </div>
                )}
              </div>

              {previewTheme && authService.authMode !== 'guest' && (
                <p className="text-xs mt-2 text-themed-warning">
                  {t('management.themes.previewActive')}
                </p>
              )}
            </div>

            {/* Installed Themes */}
            <div>
              <div className="flex flex-wrap items-center justify-between gap-2 mb-3">
                <h4 className="text-sm font-medium text-themed-secondary">{t('management.themes.installedThemes')}</h4>
                <span className="text-xs text-themed-muted">
                  {systemThemes.length} {t('management.themes.system')}, {customThemes.length} {t('management.themes.custom')}
                </span>
              </div>
              <div className="grid grid-cols-1 lg:grid-cols-2 gap-3">
                {themes.map((theme) => (
                  <ThemeCard
                    key={theme.meta.id}
                    theme={theme}
                    isActive={currentTheme === theme.meta.id && !previewTheme}
                    isPreviewing={previewTheme === theme.meta.id}
                    isSystem={isSystemTheme(theme.meta.id)}
                    isAuthenticated={isAuthenticated}
                    isGuest={authService.authMode === 'guest'}
                    themeActionMenu={themeActionMenu}
                    currentMenuId={theme.meta.id}
                    onApplyTheme={handleThemeChange}
                    onPreview={handlePreview}
                    onEdit={handleEditTheme}
                    onExport={handleExportTheme}
                    onDelete={handleDelete}
                    onMenuToggle={setThemeActionMenu}
                  />
                ))}
              </div>
            </div>

            {/* Community Themes */}
            <CommunityThemeImporter
              isAuthenticated={isAuthenticated}
              onThemeImported={loadThemes}
              installedThemes={themes}
              autoCheckUpdates={true}
            />

            {/* Upload Section */}
            {isAuthenticated && (
              <div>
                <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-2 mb-3">
                  <h4 className="text-sm font-medium text-themed-secondary">{t('management.themes.uploadCustomTheme')}</h4>
                  <Button
                    variant="subtle"
                    size="xs"
                    leftSection={<Download className="w-3 h-3" />}
                    onClick={downloadSampleTheme}
                    className="self-start sm:self-auto"
                  >
                    {t('management.themes.downloadSample')}
                  </Button>
                </div>
                <div
                  className="rounded-lg p-6 text-center transition-all border-2 border-dashed"
                  style={{
                    borderColor: dragActive ? 'var(--theme-secondary)' : 'var(--theme-border-secondary)',
                    backgroundColor: dragActive ? 'var(--theme-secondary-bg)' : 'transparent'
                  }}
                  onDragEnter={handleDrag}
                  onDragLeave={handleDrag}
                  onDragOver={handleDrag}
                  onDrop={handleDrop}
                >
                  <div className="w-12 h-12 rounded-lg mx-auto mb-3 flex items-center justify-center bg-themed-tertiary">
                    <FileText className="w-6 h-6 text-themed-muted" />
                  </div>
                  <p className="text-sm text-themed-secondary mb-1">
                    {t('management.themes.dropzone.title')}
                  </p>
                  <p className="text-xs text-themed-muted mb-3">{t('management.themes.dropzone.format')}</p>
                  <input
                    ref={fileInputRef}
                    type="file"
                    accept=".toml"
                    onChange={(e) => e.target.files?.[0] && handleFile(e.target.files[0])}
                    className="hidden"
                  />
                  <Button
                    variant="filled"
                    color="purple"
                    size="sm"
                    onClick={() => fileInputRef.current?.click()}
                    disabled={loading}
                    loading={loading}
                  >
                    {t('management.themes.browseFiles')}
                  </Button>
                </div>
              </div>
            )}

            {!isAuthenticated && (
              <Alert color="yellow">
                {t('management.themes.authRequired')}
              </Alert>
            )}
          </div>
        ) : (
          /* Customize Tab */
          <div className="space-y-4">
            <Alert color="blue">
              {t('management.themes.customize.selectThemeHint')}
            </Alert>

            {/* Quick Actions */}
            <div className="p-4 rounded-lg border bg-themed-tertiary border-themed-secondary">
              <h4 className="text-sm font-semibold text-themed-primary mb-3">{t('management.themes.customize.quickActions')}</h4>
              <div className="flex flex-col sm:flex-row gap-2 sm:flex-wrap">
                <Button
                  variant="default"
                  size="sm"
                  leftSection={<Plus className="w-4 h-4" />}
                  onClick={openCreateModal}
                  disabled={!isAuthenticated}
                  className="w-full sm:w-auto"
                >
                  {t('management.themes.customize.createNewTheme')}
                </Button>
                <Button
                  variant="default"
                  size="sm"
                  leftSection={<Download className="w-4 h-4" />}
                  onClick={downloadSampleTheme}
                  className="w-full sm:w-auto"
                >
                  {t('management.themes.customize.downloadSample')}
                </Button>
                {themes.find((t) => t.meta.id === currentTheme) && !isSystemTheme(currentTheme) && (
                  <Button
                    variant="default"
                    size="sm"
                    leftSection={<Edit className="w-4 h-4" />}
                    onClick={() => handleEditTheme(themes.find((t) => t.meta.id === currentTheme)!)}
                    disabled={!isAuthenticated}
                    className="w-full sm:w-auto"
                  >
                    {t('management.themes.customize.editCurrentTheme')}
                  </Button>
                )}
              </div>
            </div>

            {/* Color Groups Overview */}
            <div className="p-4 rounded-lg border bg-themed-tertiary border-themed-secondary">
              <h4 className="text-sm font-semibold text-themed-primary mb-2">{t('management.themes.customize.colorGroups')}</h4>
              <p className="text-xs text-themed-muted mb-4">
                {t('management.themes.customize.colorGroupsInfo', {
                  totalColors: colorGroups.reduce((acc, g) => acc + g.colors.length, 0),
                  groupCount: colorGroups.length
                })}
              </p>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-2">
                {colorGroups.map((group) => {
                  const Icon = group.icon;
                  return (
                    <div
                      key={group.name}
                      className="flex items-start gap-3 p-3 rounded-lg transition-colors hover:bg-themed-hover"
                    >
                      <div className="w-8 h-8 rounded-lg flex items-center justify-center flex-shrink-0 bg-themed-secondary">
                        <Icon className="w-4 h-4 text-themed-accent" />
                      </div>
                      <div className="min-w-0">
                        <span className="text-sm text-themed-primary font-medium capitalize block">
                          {group.name.replace(/([A-Z])/g, ' $1').trim()}
                        </span>
                        <span className="text-xs text-themed-muted">
                          {group.colors.length} {t('management.themes.customize.colors')}
                        </span>
                      </div>
                    </div>
                  );
                })}
              </div>
            </div>
          </div>
        )}
      </Card>

      {/* Modals */}
      <CreateThemeModal
        opened={createModalOpen}
        onClose={() => setCreateModalOpen(false)}
        onSave={handleCreateTheme}
        isAuthenticated={isAuthenticated}
        newTheme={newTheme}
        setNewTheme={setNewTheme}
        organizationMode={organizationMode}
        setOrganizationMode={setOrganizationMode}
        selectedPage={selectedPage}
        setSelectedPage={setSelectedPage}
        expandedGroups={expandedGroups}
        toggleGroup={toggleGroup}
        copiedColor={copiedColor}
        copyColor={copyColor}
        loading={loading}
      />

      <EditThemeModal
        opened={editModalOpen}
        onClose={() => {
          setEditModalOpen(false);
          setEditingTheme(null);
        }}
        onSave={handleSaveEditedTheme}
        isAuthenticated={isAuthenticated}
        editingTheme={editingTheme}
        editedTheme={editedTheme}
        setEditedTheme={setEditedTheme}
        editOrganizationMode={editOrganizationMode}
        setEditOrganizationMode={setEditOrganizationMode}
        editSelectedPage={editSelectedPage}
        setEditSelectedPage={setEditSelectedPage}
        expandedGroups={expandedGroups}
        toggleGroup={toggleGroup}
        copiedColor={copiedColor}
        copyColor={copyColor}
        loading={loading}
      />

      <DeleteConfirmModal
        opened={!!themePendingDeletion}
        onClose={() => setThemePendingDeletion(null)}
        onConfirm={confirmDelete}
        themeName={themePendingDeletion?.name || null}
        loading={loading}
      />

      <DeleteConfirmModal
        opened={showCleanupModal}
        onClose={() => setShowCleanupModal(false)}
        onConfirm={confirmCleanup}
        themeName="all custom themes"
        loading={loading}
      />
    </>
  );
};

export default ThemeManager;

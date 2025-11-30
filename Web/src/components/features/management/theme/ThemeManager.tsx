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
  Bell,
  FileText,
  Settings2,
  Moon,
  Sun
} from 'lucide-react';
import themeService from '@services/theme.service';
import authService from '@services/auth.service';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Checkbox } from '@components/ui/Checkbox';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { HelpPopover, HelpSection, HelpNote, HelpKeyword } from '@components/ui/HelpPopover';
import { API_BASE } from '@utils/constants';
import { Tooltip } from '@components/ui/Tooltip';
import { ThemeCard } from './ThemeCard';
import CreateThemeModal from '@components/modals/theme/CreateThemeModal';
import EditThemeModal from '@components/modals/theme/EditThemeModal';
import { DeleteConfirmModal } from '@components/modals/theme/DeleteConfirmModal';
import { CommunityThemeImporter } from './CommunityThemeImporter';
import { colorGroups } from './constants';
import { type Theme, type ThemeManagerProps } from './types';
import { useNotifications } from '@contexts/NotificationsContext';

const ThemeManager: React.FC<ThemeManagerProps> = ({ isAuthenticated }) => {
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
  const [sharpCornersEnabled, setSharpCornersEnabled] = useState(false);
  const [tooltipsDisabled, setTooltipsDisabled] = useState(false);
  const [picsAlwaysVisible, setPicsAlwaysVisible] = useState(false);
  const [disableStickyNotifications, setDisableStickyNotifications] = useState(false);

  const [editedTheme, setEditedTheme] = useState<any>({});
  const [newTheme, setNewTheme] = useState<any>({
    name: '',
    description: '',
    author: '',
    version: '1.0.0',
    isDark: true,
    customCSS: ''
  });

  const fileInputRef = useRef<HTMLInputElement>(null);

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

    // Load current option states (using sync versions for immediate display)
    setSharpCornersEnabled(themeService.getSharpCornersSync());
    setTooltipsDisabled(themeService.getDisableTooltipsSync());
    setPicsAlwaysVisible(themeService.getPicsAlwaysVisibleSync());
    setDisableStickyNotifications(themeService.getDisableStickyNotificationsSync());

    // Listen for live preference changes from admin
    const handlePreferenceChange = (event: any) => {
      const { key, value } = event.detail;

      switch (key) {
        case 'selectedTheme':
          if (value) {
            setCurrentTheme(value);
            setPreviewTheme(null);
          }
          break;
        case 'sharpCorners':
          setSharpCornersEnabled(value);
          break;
        case 'disableFocusOutlines':
          // This is handled by theme service, no UI state to update
          break;
        case 'disableTooltips':
          setTooltipsDisabled(value);
          break;
        case 'picsAlwaysVisible':
          setPicsAlwaysVisible(value);
          break;
        case 'disableStickyNotifications':
          setDisableStickyNotifications(value);
          break;
      }
    };

    window.addEventListener('preference-changed', handlePreferenceChange);

    return () => {
      window.removeEventListener('preference-changed', handlePreferenceChange);
    };
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
    try {
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

      const themeData = {
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
        colors,
        css: customCSS ? { content: customCSS } : undefined
      };

      // Convert to TOML and upload
      const toml = themeService.exportTheme(themeData as any);
      const blob = new Blob([toml], { type: 'application/toml' });
      const file = new File([blob], `${newThemeId}.toml`, { type: 'application/toml' });

      const formData = new FormData();
      formData.append('file', file);

      const response = await fetch(`${API_BASE}/themes/upload`, {
        method: 'POST',
        headers: authService.getAuthHeaders(),
        body: formData
      });

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

      // Generate a safe ID from the theme name
      const themeId = name.toLowerCase().replace(/[^a-z0-9-_]/g, '-');

      const themeData = {
        meta: {
          id: themeId,
          name,
          description,
          author,
          version,
          isDark
        },
        colors,
        css: customCSS ? { content: customCSS } : undefined
      };

      // Convert to TOML and upload
      const toml = themeService.exportTheme(themeData as any);
      const blob = new Blob([toml], { type: 'application/toml' });
      const file = new File([blob], `${themeId}.toml`, { type: 'application/toml' });

      const formData = new FormData();
      formData.append('file', file);

      const response = await fetch(`${API_BASE}/themes/upload`, {
        method: 'POST',
        headers: authService.getAuthHeaders(),
        body: formData
      });

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
      const response = await fetch(`${API_BASE}/themes/${themePendingDeletion.id}`, {
        method: 'DELETE',
        headers: authService.getAuthHeaders()
      });

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
        message: 'Failed to delete theme',
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
        await fetch(`${API_BASE}/themes/${theme.meta.id}`, {
          method: 'DELETE',
          headers: authService.getAuthHeaders()
        });
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
        message: 'Please upload a .toml file',
        details: { notificationType: 'error' }
      });
      return;
    }

    if (file.size > 1024 * 1024) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: 'File size must be less than 1MB',
        details: { notificationType: 'error' }
      });
      return;
    }

    setLoading(true);
    try {
      const formData = new FormData();
      formData.append('file', file);

      const response = await fetch(`${API_BASE}/themes/upload`, {
        method: 'POST',
        headers: authService.getAuthHeaders(),
        body: formData
      });

      if (!response.ok) {
        const error = await response.json();
        throw new Error(error.error || 'Upload failed');
      }

      addNotification({
        type: 'generic',
        status: 'completed',
        message: 'Theme uploaded successfully!',
        details: { notificationType: 'success' }
      });
      await loadThemes();
    } catch (error: any) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: error.message || 'Failed to upload theme',
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

  // Options Handlers
  const handleSharpCornersToggle = (enabled: boolean) => {
    setSharpCornersEnabled(enabled);
    themeService.setSharpCorners(enabled);
  };

  const handleTooltipsToggle = (enabled: boolean) => {
    setTooltipsDisabled(enabled);
    themeService.setDisableTooltips(enabled);
  };

  const handlePicsAlwaysVisibleToggle = (enabled: boolean) => {
    setPicsAlwaysVisible(enabled);
    themeService.setPicsAlwaysVisible(enabled);
  };

  const handleDisableStickyNotificationsToggle = (enabled: boolean) => {
    setDisableStickyNotifications(enabled);
    themeService.setDisableStickyNotifications(enabled);
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
              <h3 className="text-lg font-semibold text-themed-primary">Theme Management</h3>
              <p className="text-xs text-themed-muted">{themes.length} themes available</p>
            </div>
            <HelpPopover position="left" width={300}>
              <HelpSection title="Theme Options">
                <HelpKeyword color="blue">System themes</HelpKeyword> are built-in and cannot be deleted.{' '}
                <HelpKeyword color="purple">Custom themes</HelpKeyword> can be created, edited, or imported
                from the community.
              </HelpSection>

              <HelpSection title="Preview Mode" variant="subtle">
                Preview themes without committing — click{' '}
                <HelpKeyword color="cyan">Apply</HelpKeyword> to save your choice permanently.
              </HelpSection>

              <HelpNote type="info">
                Theme files use TOML format. Download a sample to see the structure.
              </HelpNote>
            </HelpPopover>
          </div>
          <div className="flex items-center gap-2 flex-shrink-0">
            {isAuthenticated ? (
              <>
                <Tooltip content="Create new theme" position="bottom">
                  <Button
                    variant="default"
                    size="sm"
                    onClick={openCreateModal}
                  >
                    <Plus className="w-4 h-4" />
                  </Button>
                </Tooltip>
                <Tooltip content="Delete all custom themes" position="bottom">
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
              <Tooltip content="Authentication required" position="bottom">
                <Button variant="default" size="sm" disabled>
                  <Lock className="w-4 h-4" />
                </Button>
              </Tooltip>
            )}
            <Tooltip content="Refresh themes" position="bottom">
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
        <div
          className="flex gap-1 mb-6 p-1 rounded-lg"
          style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
        >
          <button
            onClick={() => setActiveTab('themes')}
            className={`flex-1 flex items-center justify-center gap-2 px-4 py-2 rounded-md text-sm font-medium transition-all ${
              activeTab === 'themes' ? 'text-themed-primary shadow-sm' : 'text-themed-muted hover:text-themed-secondary'
            }`}
            style={
              activeTab === 'themes'
                ? { backgroundColor: 'var(--theme-bg-secondary)' }
                : { backgroundColor: 'transparent' }
            }
          >
            <Layers className="w-4 h-4" />
            Themes
          </button>
          <button
            onClick={() => setActiveTab('customize')}
            className={`flex-1 flex items-center justify-center gap-2 px-4 py-2 rounded-md text-sm font-medium transition-all ${
              activeTab === 'customize' ? 'text-themed-primary shadow-sm' : 'text-themed-muted hover:text-themed-secondary'
            }`}
            style={
              activeTab === 'customize'
                ? { backgroundColor: 'var(--theme-bg-secondary)' }
                : { backgroundColor: 'transparent' }
            }
          >
            <Brush className="w-4 h-4" />
            Customize
          </button>
        </div>

        {activeTab === 'themes' ? (
          <div className="space-y-6">
            {/* Guest User Alert */}
            {authService.authMode === 'guest' && (
              <Alert color="blue">
                <div>
                  <p className="text-sm font-medium mb-1">Guest Mode - Theme Selection Disabled</p>
                  <p className="text-sm">
                    Guest users cannot change themes. The theme is set by the administrator.
                  </p>
                </div>
              </Alert>
            )}

            {/* Current Theme Section */}
            <div
              className="p-4 rounded-lg border"
              style={{
                backgroundColor: 'var(--theme-bg-tertiary)',
                borderColor: 'var(--theme-border-secondary)'
              }}
            >
              <div className="flex items-center gap-2 mb-4">
                <Settings2 className="w-4 h-4 text-themed-accent" />
                <span className="text-sm font-medium text-themed-primary">Active Theme</span>
              </div>

              <div className="flex flex-col sm:flex-row sm:items-center gap-3 sm:gap-4">
                <div className="flex-1 min-w-0">
                  <EnhancedDropdown
                    options={themes.map((theme) => ({
                      value: theme.meta.id,
                      label: `${theme.meta.name}${isSystemTheme(theme.meta.id) ? ' (System)' : ''}${previewTheme === theme.meta.id ? ' (Preview)' : ''}`
                    }))}
                    value={previewTheme || currentTheme}
                    onChange={handleThemeChange}
                    placeholder="Select a theme"
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
                      ].map((color, i) => (
                        <div
                          key={i}
                          className="w-5 h-5 rounded"
                          style={{ backgroundColor: color || '#666' }}
                        />
                      ))}
                    </div>
                  </div>
                )}
              </div>

              {previewTheme && authService.authMode !== 'guest' && (
                <p className="text-xs mt-2 text-themed-warning">
                  Preview mode active. Select a theme to apply it permanently.
                </p>
              )}
            </div>

            {/* Preferences Section */}
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
              {/* Visual Preferences */}
              <div
                className="p-4 rounded-lg border"
                style={{
                  backgroundColor: 'var(--theme-bg-tertiary)',
                  borderColor: 'var(--theme-border-secondary)'
                }}
              >
                <div className="flex items-center gap-2 mb-4">
                  <Brush className="w-4 h-4 text-themed-accent" />
                  <span className="text-sm font-medium text-themed-primary">Visual</span>
                </div>
                <div className="space-y-3">
                  <label className="flex items-center gap-3 cursor-pointer">
                    <Checkbox
                      checked={sharpCornersEnabled}
                      onChange={(e) => handleSharpCornersToggle(e.target.checked)}
                      variant="rounded"
                    />
                    <div>
                      <span className="text-sm text-themed-primary">Sharp Corners</span>
                      <p className="text-xs text-themed-muted">Square corners instead of rounded</p>
                    </div>
                  </label>
                  <label className="flex items-center gap-3 cursor-pointer">
                    <Checkbox
                      checked={!tooltipsDisabled}
                      onChange={(e) => handleTooltipsToggle(!e.target.checked)}
                      variant="rounded"
                    />
                    <div>
                      <span className="text-sm text-themed-primary">Tooltips</span>
                      <p className="text-xs text-themed-muted">Show helpful hints on hover</p>
                    </div>
                  </label>
                </div>
              </div>

              {/* Notification Preferences */}
              <div
                className="p-4 rounded-lg border"
                style={{
                  backgroundColor: 'var(--theme-bg-tertiary)',
                  borderColor: 'var(--theme-border-secondary)'
                }}
              >
                <div className="flex items-center gap-2 mb-4">
                  <Bell className="w-4 h-4 text-themed-accent" />
                  <span className="text-sm font-medium text-themed-primary">Notifications</span>
                </div>
                <div className="space-y-3">
                  <label className="flex items-center gap-3 cursor-pointer">
                    <Checkbox
                      checked={!disableStickyNotifications}
                      onChange={(e) => handleDisableStickyNotificationsToggle(!e.target.checked)}
                      variant="rounded"
                    />
                    <div>
                      <span className="text-sm text-themed-primary">Sticky Notifications</span>
                      <p className="text-xs text-themed-muted">Fixed at top when scrolling</p>
                    </div>
                  </label>
                  <label className="flex items-center gap-3 cursor-pointer">
                    <Checkbox
                      checked={picsAlwaysVisible}
                      onChange={(e) => handlePicsAlwaysVisibleToggle(e.target.checked)}
                      variant="rounded"
                    />
                    <div>
                      <span className="text-sm text-themed-primary">Static Notifications</span>
                      <p className="text-xs text-themed-muted">Require manual dismissal</p>
                    </div>
                  </label>
                </div>
              </div>
            </div>

            {/* Installed Themes */}
            <div>
              <div className="flex flex-wrap items-center justify-between gap-2 mb-3">
                <h4 className="text-sm font-medium text-themed-secondary">Installed Themes</h4>
                <span className="text-xs text-themed-muted">
                  {systemThemes.length} system, {customThemes.length} custom
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
                  <h4 className="text-sm font-medium text-themed-secondary">Upload Custom Theme</h4>
                  <Button
                    variant="subtle"
                    size="xs"
                    leftSection={<Download className="w-3 h-3" />}
                    onClick={downloadSampleTheme}
                    className="self-start sm:self-auto"
                  >
                    Download Sample
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
                  <div
                    className="w-12 h-12 rounded-lg mx-auto mb-3 flex items-center justify-center"
                    style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                  >
                    <FileText className="w-6 h-6 text-themed-muted" />
                  </div>
                  <p className="text-sm text-themed-secondary mb-1">
                    Drop a theme file here, or click to browse
                  </p>
                  <p className="text-xs text-themed-muted mb-3">TOML format, max 1MB</p>
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
                    Browse Files
                  </Button>
                </div>
              </div>
            )}

            {!isAuthenticated && (
              <Alert color="yellow">
                Authentication required to create, upload, or delete custom themes
              </Alert>
            )}
          </div>
        ) : (
          /* Customize Tab */
          <div className="space-y-4">
            <Alert color="blue">
              Select a theme above and click Edit to customize its colors
            </Alert>

            {/* Quick Actions */}
            <div
              className="p-4 rounded-lg border"
              style={{
                backgroundColor: 'var(--theme-bg-tertiary)',
                borderColor: 'var(--theme-border-secondary)'
              }}
            >
              <h4 className="text-sm font-semibold text-themed-primary mb-3">Quick Actions</h4>
              <div className="flex flex-col sm:flex-row gap-2 sm:flex-wrap">
                <Button
                  variant="default"
                  size="sm"
                  leftSection={<Plus className="w-4 h-4" />}
                  onClick={openCreateModal}
                  disabled={!isAuthenticated}
                  className="w-full sm:w-auto"
                >
                  Create New Theme
                </Button>
                <Button
                  variant="default"
                  size="sm"
                  leftSection={<Download className="w-4 h-4" />}
                  onClick={downloadSampleTheme}
                  className="w-full sm:w-auto"
                >
                  Download Sample
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
                    Edit Current Theme
                  </Button>
                )}
              </div>
            </div>

            {/* Color Groups Overview */}
            <div
              className="p-4 rounded-lg border"
              style={{
                backgroundColor: 'var(--theme-bg-tertiary)',
                borderColor: 'var(--theme-border-secondary)'
              }}
            >
              <h4 className="text-sm font-semibold text-themed-primary mb-2">Color Groups</h4>
              <p className="text-xs text-themed-muted mb-4">
                Themes contain {colorGroups.reduce((acc, g) => acc + g.colors.length, 0)} customizable
                colors organized into {colorGroups.length} groups
              </p>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-2">
                {colorGroups.map((group) => {
                  const Icon = group.icon;
                  return (
                    <div
                      key={group.name}
                      className="flex items-start gap-3 p-3 rounded-lg transition-colors hover:bg-themed-hover"
                    >
                      <div
                        className="w-8 h-8 rounded-lg flex items-center justify-center flex-shrink-0"
                        style={{ backgroundColor: 'var(--theme-bg-secondary)' }}
                      >
                        <Icon className="w-4 h-4 text-themed-accent" />
                      </div>
                      <div className="min-w-0">
                        <span className="text-sm text-themed-primary font-medium capitalize block">
                          {group.name.replace(/([A-Z])/g, ' $1').trim()}
                        </span>
                        <span className="text-xs text-themed-muted">
                          {group.colors.length} colors
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

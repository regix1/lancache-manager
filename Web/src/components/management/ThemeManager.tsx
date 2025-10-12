import React, { useState, useEffect, useRef, useCallback } from 'react';
import {
  Palette,
  Upload,
  Download,
  RefreshCw,
  Lock,
  Plus,
  Sparkles,
  Layers,
  Brush,
  Info,
  Edit
} from 'lucide-react';
import themeService from '../../services/theme.service';
import authService from '../../services/auth.service';
import { Alert } from '../ui/Alert';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { Checkbox } from '../ui/Checkbox';
import { EnhancedDropdown } from '../ui/EnhancedDropdown';
import { API_BASE } from '../../utils/constants';
import { ThemeCard } from './theme/ThemeCard';
import CreateThemeModal from './theme/CreateThemeModal';
import EditThemeModal from './theme/EditThemeModal';
import { DeleteConfirmModal } from './theme/DeleteConfirmModal';
import { CommunityThemeImporter } from './theme/CommunityThemeImporter';
import { colorGroups } from './theme/constants';
import { Theme, ThemeManagerProps } from './theme/types';

const ThemeManager: React.FC<ThemeManagerProps> = ({ isAuthenticated }) => {
  // State Management
  const [themes, setThemes] = useState<Theme[]>([]);
  const [currentTheme, setCurrentTheme] = useState('dark-default');
  const [loading, setLoading] = useState(false);
  const [dragActive, setDragActive] = useState(false);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [uploadSuccess, setUploadSuccess] = useState<string | null>(null);
  const [previewTheme, setPreviewTheme] = useState<string | null>(null);
  const [createModalOpen, setCreateModalOpen] = useState(false);
  const [editModalOpen, setEditModalOpen] = useState(false);
  const [themePendingDeletion, setThemePendingDeletion] = useState<{ id: string; name: string } | null>(null);
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
    const saved = themeService.getCurrentThemeId();
    if (saved) setCurrentTheme(saved);

    // Load current option states
    setSharpCornersEnabled(themeService.getSharpCorners());
    setTooltipsDisabled(themeService.getDisableTooltips());
    setPicsAlwaysVisible(themeService.getPicsAlwaysVisible());
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
      window.location.reload();
    } catch (error) {
      console.error('Failed to change theme:', error);
    }
  };

  const handlePreview = async (themeId: string) => {
    if (previewTheme === themeId) {
      await themeService.setTheme(currentTheme);
      setPreviewTheme(null);
      window.location.reload();
    } else {
      await themeService.setTheme(themeId);
      setPreviewTheme(themeId);
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

      const themeData = {
        meta: {
          id: editingTheme.meta.id,
          name,
          description,
          author,
          version,
          isDark
        },
        colors,
        css: customCSS ? { content: customCSS } : undefined
      };

      // Convert to TOML and upload (will overwrite existing file)
      const toml = themeService.exportTheme(themeData as any);
      const blob = new Blob([toml], { type: 'application/toml' });
      const file = new File([blob], `${editingTheme.meta.id}.toml`, { type: 'application/toml' });

      const formData = new FormData();
      formData.append('file', file);

      const response = await fetch(`${API_BASE}/theme/upload`, {
        method: 'POST',
        headers: authService.getAuthHeaders(),
        body: formData
      });

      if (!response.ok) throw new Error('Failed to update theme');

      await loadThemes();
      setEditModalOpen(false);
      setEditingTheme(null);

      if (currentTheme === editingTheme.meta.id || previewTheme === editingTheme.meta.id) {
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

      const response = await fetch(`${API_BASE}/theme/upload`, {
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
      const response = await fetch(`${API_BASE}/theme/${themePendingDeletion.id}`, {
        method: 'DELETE',
        headers: authService.getAuthHeaders()
      });

      if (!response.ok) throw new Error('Failed to delete theme');

      if (currentTheme === themePendingDeletion.id) {
        await themeService.setTheme('dark-default');
        setCurrentTheme('dark-default');
      }
      if (previewTheme === themePendingDeletion.id) {
        setPreviewTheme(null);
      }

      await loadThemes();
      setThemePendingDeletion(null);
    } catch (error) {
      console.error('Error deleting theme:', error);
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
      const customThemes = themes.filter(t => !isSystemTheme(t.meta.id));

      for (const theme of customThemes) {
        await fetch(`${API_BASE}/theme/${theme.meta.id}`, {
          method: 'DELETE',
          headers: authService.getAuthHeaders()
        });
      }

      await themeService.setTheme('dark-default');
      setCurrentTheme('dark-default');
      setPreviewTheme(null);
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
    setUploadError(null);
    setUploadSuccess(null);

    if (!file.name.endsWith('.toml')) {
      setUploadError('Please upload a .toml file');
      return;
    }

    if (file.size > 1024 * 1024) {
      setUploadError('File size must be less than 1MB');
      return;
    }

    setLoading(true);
    try {
      const formData = new FormData();
      formData.append('file', file);

      const response = await fetch(`${API_BASE}/theme/upload`, {
        method: 'POST',
        headers: authService.getAuthHeaders(),
        body: formData
      });

      if (!response.ok) {
        const error = await response.json();
        throw new Error(error.error || 'Upload failed');
      }

      setUploadSuccess('Theme uploaded successfully!');
      await loadThemes();
    } catch (error: any) {
      setUploadError(error.message || 'Failed to upload theme');
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
    const sampleTheme = themes.find(t => t.meta.id === 'dark-default');
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

  // Utility Functions
  const toggleGroup = (groupName: string) => {
    setExpandedGroups(prev =>
      prev.includes(groupName)
        ? prev.filter(g => g !== groupName)
        : [...prev, groupName]
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
    const currentThemeData = themes.find(t => t.meta.id === currentTheme);
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

  return (
    <>
      <Card>
        <div className="flex items-center justify-between mb-4">
          <div className="flex items-center space-x-2">
            <Palette className="w-5 h-5 icon-purple" />
            <h3 className="text-lg font-semibold text-themed-primary">Theme Management</h3>
          </div>
          <div className="flex items-center space-x-2">
            {isAuthenticated ? (
              <>
                <button
                  onClick={openCreateModal}
                  className="p-2 rounded-lg transition-colors"
                  style={{
                    color: 'var(--theme-text-muted)',
                    backgroundColor: 'transparent'
                  }}
                  onMouseEnter={(e) =>
                    (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
                  }
                  onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
                  title="Create new theme"
                >
                  <Plus className="w-4 h-4" />
                </button>
                <button
                  onClick={cleanupThemes}
                  disabled={loading}
                  className="p-2 rounded-lg transition-colors"
                  style={{
                    color: 'var(--theme-text-muted)',
                    backgroundColor: 'transparent'
                  }}
                  onMouseEnter={(e) =>
                    (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
                  }
                  onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
                  title="Delete all custom themes"
                >
                  <Sparkles className="w-4 h-4" />
                </button>
              </>
            ) : (
              <button
                disabled
                className="p-2 rounded-lg transition-colors opacity-50 cursor-not-allowed"
                style={{
                  color: 'var(--theme-text-muted)',
                  backgroundColor: 'transparent'
                }}
                title="Authentication required to create themes"
              >
                <Lock className="w-4 h-4" />
              </button>
            )}
            <button
              onClick={() => loadThemes()}
              disabled={loading}
              className="p-2 rounded-lg transition-colors"
              style={{
                color: 'var(--theme-text-muted)',
                backgroundColor: 'transparent'
              }}
              onMouseEnter={(e) =>
                (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
              }
              onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
              title="Refresh themes"
            >
              <RefreshCw className={`w-4 h-4 ${loading ? 'animate-spin' : ''}`} />
            </button>
          </div>
        </div>

        {/* Tab Navigation */}
        <div className="flex gap-2 mb-6 border-b" style={{ borderColor: 'var(--theme-border-secondary)' }}>
          <button
            onClick={() => setActiveTab('themes')}
            className={`px-4 py-2 font-medium transition-colors ${
              activeTab === 'themes'
                ? 'text-themed-accent'
                : 'text-themed-muted hover:text-themed-primary'
            }`}
            style={activeTab === 'themes' ? { borderBottom: '2px solid var(--theme-primary)' } : {}}
          >
            <Layers className="w-4 h-4 inline-block mr-2" />
            Themes ({themes.length})
          </button>
          <button
            onClick={() => setActiveTab('customize')}
            className={`px-4 py-2 font-medium transition-colors ${
              activeTab === 'customize'
                ? 'text-themed-accent'
                : 'text-themed-muted hover:text-themed-primary'
            }`}
            style={activeTab === 'customize' ? { borderBottom: '2px solid var(--theme-primary)' } : {}}
          >
            <Brush className="w-4 h-4 inline-block mr-2" />
            Customize
          </button>
        </div>

        {activeTab === 'themes' ? (
          <>
            {/* Active Theme Selector */}
            <div className="mb-6 p-4 rounded-lg bg-themed-tertiary">
              <label className="block text-sm font-medium mb-2 text-themed-secondary">
                Active Theme
              </label>
              <EnhancedDropdown
                options={themes.map((theme) => ({
                  value: theme.meta.id,
                  label: `${theme.meta.name}${theme.meta.author && theme.meta.author !== 'System' ? ` by ${theme.meta.author}` : ''}${isSystemTheme(theme.meta.id) ? ' (System)' : ''}${previewTheme === theme.meta.id ? ' (Preview)' : ''}`
                }))}
                value={previewTheme || currentTheme}
                onChange={handleThemeChange}
                placeholder="Select a theme"
                className="w-full"
              />
              {previewTheme && (
                <p className="text-xs mt-2 text-themed-warning">
                  Preview mode active. Select a theme to apply it permanently.
                </p>
              )}
            </div>

            {/* Options */}
            <div className="mb-6 p-4 rounded-lg bg-themed-tertiary">
              <label className="block text-sm font-medium mb-3 text-themed-secondary">
                Options
              </label>
              <div className="flex flex-col sm:flex-row gap-4">
                <Checkbox
                  checked={sharpCornersEnabled}
                  onChange={(e) => handleSharpCornersToggle(e.target.checked)}
                  variant="rounded"
                  label="Sharp Corners"
                />
                <Checkbox
                  checked={tooltipsDisabled}
                  onChange={(e) => handleTooltipsToggle(e.target.checked)}
                  variant="rounded"
                  label="Disable Tooltips"
                />
                <Checkbox
                  checked={picsAlwaysVisible}
                  onChange={(e) => handlePicsAlwaysVisibleToggle(e.target.checked)}
                  variant="rounded"
                  label="Steam PICS Always Visible"
                />
              </div>
            </div>

            {/* Theme Cards Grid */}
            <div className="mb-6">
              <h4 className="text-sm font-medium mb-3 text-themed-secondary">
                Installed Themes
              </h4>
              <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
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

            {/* Community Themes Section */}
            <div className="mb-6">
              <CommunityThemeImporter
                isAuthenticated={isAuthenticated}
                onThemeImported={loadThemes}
                installedThemes={themes}
              />
            </div>

            {/* Upload Custom Theme Section */}
            {isAuthenticated && (
              <>
                <div className="mb-4">
                  <h4 className="text-sm font-medium mb-3 text-themed-secondary">
                    Upload Custom Theme
                  </h4>
                  <div
                    className={`border-dashed rounded-lg p-8 text-center transition-colors ${
                      dragActive ? 'bg-purple-900 bg-opacity-20' : ''
                    }`}
                    style={{
                      border: dragActive ? '2px dashed var(--theme-primary)' : '2px dashed var(--theme-border-secondary)'
                    }}
                    onDragEnter={handleDrag}
                    onDragLeave={handleDrag}
                    onDragOver={handleDrag}
                    onDrop={handleDrop}
                  >
                    <Upload className="w-12 h-12 mx-auto mb-3 text-themed-muted" />
                    <p className="mb-2 text-themed-secondary">
                      Drag and drop a theme file here, or click to browse
                    </p>
                    <p className="text-xs mb-3 text-themed-muted">TOML format, max 1MB</p>
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
                      onClick={() => fileInputRef.current?.click()}
                      disabled={loading}
                      loading={loading}
                    >
                      Browse Files
                    </Button>
                  </div>

                  {uploadError && (
                    <Alert color="red" className="mt-3">
                      {uploadError}
                    </Alert>
                  )}
                  {uploadSuccess && (
                    <Alert color="green" className="mt-3">
                      {uploadSuccess}
                    </Alert>
                  )}
                </div>

                <div className="flex justify-center">
                  <Button
                    variant="subtle"
                    leftSection={<Download className="w-4 h-4" />}
                    onClick={downloadSampleTheme}
                  >
                    Download Sample TOML Theme
                  </Button>
                </div>
              </>
            )}

            {!isAuthenticated && (
              <Alert color="yellow">
                Authentication required to create, upload, or delete custom themes
              </Alert>
            )}
          </>
        ) : (
          /* Customize Tab */
          <div className="space-y-4">
            <Alert color="blue">
              <div className="flex items-center gap-2">
                <Info className="w-4 h-4" />
                <span>Select a theme above and click Edit to customize its colors</span>
              </div>
            </Alert>

            <div className="p-4 rounded-lg bg-themed-tertiary">
              <h4 className="text-sm font-semibold text-themed-primary mb-2">Quick Actions</h4>
              <div className="flex gap-2 flex-wrap">
                <Button
                  variant="default"
                  size="sm"
                  leftSection={<Plus className="w-4 h-4" />}
                  onClick={openCreateModal}
                  disabled={!isAuthenticated}
                >
                  Create New Theme
                </Button>
                <Button
                  variant="default"
                  size="sm"
                  leftSection={<Download className="w-4 h-4" />}
                  onClick={downloadSampleTheme}
                >
                  Download Sample
                </Button>
                {themes.find(t => t.meta.id === currentTheme) && !isSystemTheme(currentTheme) && (
                  <Button
                    variant="default"
                    size="sm"
                    leftSection={<Edit className="w-4 h-4" />}
                    onClick={() => handleEditTheme(themes.find(t => t.meta.id === currentTheme)!)}
                    disabled={!isAuthenticated}
                  >
                    Edit Current Theme
                  </Button>
                )}
              </div>
            </div>

            <div className="p-4 rounded-lg bg-themed-tertiary">
              <h4 className="text-sm font-semibold text-themed-primary mb-3">Color Groups Overview</h4>
              <div className="text-xs text-themed-muted mb-3">Themes contain {colorGroups.reduce((acc, g) => acc + g.colors.length, 0)} customizable colors organized into groups:</div>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-2">
                {colorGroups.map((group) => {
                  const Icon = group.icon;
                  return (
                    <div key={group.name} className="flex items-start gap-2 text-sm p-2 rounded hover:bg-themed-hover transition-colors">
                      <Icon className="w-4 h-4 text-themed-accent mt-0.5" />
                      <div>
                        <span className="text-themed-primary font-medium capitalize">
                          {group.name.replace(/([A-Z])/g, ' $1').trim()}
                        </span>
                        <span className="text-themed-muted text-xs block">
                          {group.colors.length} colors - {group.description}
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

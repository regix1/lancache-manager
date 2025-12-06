import React, { useState, useCallback } from 'react';
import {
  Moon,
  Sun,
  Info,
  Layers,
  Layout,
  Search,
  X,
  ChevronDown,
  ChevronRight,
  Save
} from 'lucide-react';
import { Modal } from '../../ui/Modal';
import { Button } from '../../ui/Button';
import { Checkbox } from '../../ui/Checkbox';
import { ImprovedColorPicker } from '../../features/management/theme/ImprovedColorPicker';
import { colorGroups, pageDefinitions } from '../../features/management/theme/constants';
import { type ColorGroup } from '../../features/management/theme/types';
import { storage } from '@utils/storage';

interface CreateThemeModalProps {
  opened: boolean;
  onClose: () => void;
  onSave: () => void;
  isAuthenticated: boolean;
  newTheme: any;
  setNewTheme: React.Dispatch<React.SetStateAction<any>>;
  organizationMode: 'category' | 'page';
  setOrganizationMode: React.Dispatch<React.SetStateAction<'category' | 'page'>>;
  selectedPage: string;
  setSelectedPage: React.Dispatch<React.SetStateAction<string>>;
  expandedGroups: string[];
  toggleGroup: (groupName: string) => void;
  copiedColor: string | null;
  copyColor: (color: string) => void;
  loading: boolean;
}

const CreateThemeModal: React.FC<CreateThemeModalProps> = ({
  opened,
  onClose,
  onSave,
  isAuthenticated,
  newTheme,
  setNewTheme,
  organizationMode,
  setOrganizationMode,
  selectedPage,
  setSelectedPage,
  expandedGroups,
  toggleGroup,
  copiedColor,
  copyColor,
  loading
}) => {
  const [createSearchQuery, setCreateSearchQuery] = useState('');

  const handleColorCommit = (key: string, previousColor: string) => {
    // Save the previous color to history when user finishes editing
    const historyKey = `color_history_create_${key}`;
    const originalKey = `color_history_create_${key}_original`;
    const existingHistory = storage.getItem(historyKey);
    let history: string[] = [];

    // Save original color if this is the first time (no history exists)
    if (!existingHistory) {
      storage.setItem(originalKey, previousColor);
    }

    // Handle migration from old format (single string) to new format (array)
    if (existingHistory) {
      try {
        history = JSON.parse(existingHistory);
        // Ensure it's an array
        if (!Array.isArray(history)) {
          history = [existingHistory]; // Old format: single string
        }
      } catch {
        // Old format: plain string, not JSON
        history = [existingHistory];
      }
    }

    // Add previous color to history (max 3 recent changes, original is stored separately)
    history.unshift(previousColor);
    if (history.length > 3) {
      history.pop(); // Remove oldest (but original is safe in separate key)
    }

    storage.setItem(historyKey, JSON.stringify(history));
  };

  const handleColorChange = (key: string, value: string) => {
    // Just update the value, don't save to history on every change
    setNewTheme((prev: any) => ({ ...prev, [key]: value }));
  };

  const restoreCreatePreviousColor = (key: string) => {
    const historyKey = `color_history_create_${key}`;
    const originalKey = `color_history_create_${key}_original`;
    const existingHistory = storage.getItem(historyKey);

    if (existingHistory) {
      let history: string[] = [];

      // Handle migration from old format (single string) to new format (array)
      try {
        history = JSON.parse(existingHistory);
        if (!Array.isArray(history)) {
          history = [existingHistory]; // Old format: single string
        }
      } catch {
        // Old format: plain string, not JSON
        history = [existingHistory];
      }

      if (history.length > 0) {
        // Pop the most recent history item
        const previousColor = history.shift();

        // Update localStorage with remaining history
        if (history.length > 0) {
          storage.setItem(historyKey, JSON.stringify(history));
        } else {
          storage.removeItem(historyKey);
        }

        // Apply the previous color immediately
        if (previousColor) {
          setNewTheme((prev: any) => ({ ...prev, [key]: previousColor }));
        }
      }
    } else {
      // No recent history, restore to original color and remove it (final undo)
      const originalColor = storage.getItem(originalKey);
      if (originalColor) {
        setNewTheme((prev: any) => ({ ...prev, [key]: originalColor }));
        // Remove original after restoring to it (no more undos available)
        storage.removeItem(originalKey);
      }
    }
  };

  const getCreateColorHistory = (key: string) => {
    const historyKey = `color_history_create_${key}`;
    const originalKey = `color_history_create_${key}_original`;
    const existingHistory = storage.getItem(historyKey);

    if (existingHistory) {
      let history: string[] = [];

      // Handle migration from old format (single string) to new format (array)
      try {
        history = JSON.parse(existingHistory);
        if (!Array.isArray(history)) {
          return existingHistory; // Old format: return the string directly
        }
      } catch {
        // Old format: plain string, not JSON
        return existingHistory;
      }

      return history.length > 0 ? history[0] : null;
    }

    // No recent history, check for original color
    return storage.getItem(originalKey);
  };

  const loadPresetColors = (preset: 'dark' | 'light') => {
    if (preset === 'dark') {
      setNewTheme((prev: any) => ({
        ...prev,
        isDark: true,
        bgPrimary: '#111827',
        bgSecondary: '#1f2937',
        bgTertiary: '#374151',
        bgHover: '#4b5563',
        textPrimary: '#ffffff',
        textSecondary: '#d1d5db',
        textMuted: '#9ca3af',
        dragHandleColor: '#6b7280',
        dragHandleHover: '#00ff00',
        borderPrimary: '#374151',
        borderSecondary: '#4b5563',
        navBg: '#1f2937',
        navBorder: '#374151',
        navTabActive: '#3b82f6',
        navTabInactive: '#9ca3af',
        navTabHover: '#ffffff',
        navTabActiveBorder: '#3b82f6',
        navMobileMenuBg: '#1f2937',
        navMobileItemHover: '#374151',
        cardBg: '#1f2937',
        cardBorder: '#374151',
        cardOutline: '#3b82f6',
        inputBg: '#374151',
        inputBorder: '#4b5563',
        progressBg: '#374151',
        successBg: '#064e3b',
        successText: '#34d399',
        warningBg: '#78350f',
        warningText: '#fbbf24',
        errorBg: '#7f1d1d',
        errorText: '#fca5a5',
        infoBg: '#1e3a8a',
        infoText: '#93c5fd',
        chartBorderColor: '#1f2937',
        chartGridColor: '#374151',
        chartTextColor: '#9ca3af',
        scrollbarTrack: '#374151',
        scrollbarThumb: '#6B7280',
        scrollbarHover: '#9CA3AF'
      }));
    } else {
      setNewTheme((prev: any) => ({
        ...prev,
        isDark: false,
        bgPrimary: '#ffffff',
        bgSecondary: '#f9fafb',
        bgTertiary: '#f3f4f6',
        bgHover: '#e5e7eb',
        textPrimary: '#111827',
        textSecondary: '#374151',
        textMuted: '#6b7280',
        dragHandleColor: '#9ca3af',
        dragHandleHover: '#2563eb',
        borderPrimary: '#e5e7eb',
        borderSecondary: '#d1d5db',
        navBg: '#f9fafb',
        navBorder: '#e5e7eb',
        navTabActive: '#3b82f6',
        navTabInactive: '#6b7280',
        navTabHover: '#111827',
        navTabActiveBorder: '#3b82f6',
        navMobileMenuBg: '#f9fafb',
        navMobileItemHover: '#e5e7eb',
        cardBg: '#ffffff',
        cardBorder: '#e5e7eb',
        cardOutline: '#3b82f6',
        buttonBg: '#3b82f6',
        buttonHover: '#2563eb',
        buttonText: '#ffffff',
        inputBg: '#ffffff',
        inputBorder: '#d1d5db',
        inputFocus: '#3b82f6',
        checkboxAccent: '#3b82f6',
        checkboxBorder: '#d1d5db',
        checkboxBg: '#ffffff',
        checkboxCheckmark: '#ffffff',
        checkboxShadow: 'none',
        checkboxHoverShadow: 'none',
        checkboxHoverBg: '#f3f4f6',
        checkboxFocus: '#3b82f6',
        sliderAccent: '#3b82f6',
        sliderThumb: '#3b82f6',
        sliderTrack: '#e5e7eb',
        progressBg: '#e5e7eb',
        successBg: '#d1fae5',
        successText: '#065f46',
        warningBg: '#fef3c7',
        warningText: '#92400e',
        errorBg: '#fee2e2',
        errorText: '#991b1b',
        infoBg: '#dbeafe',
        infoText: '#1e40af',
        chartBorderColor: '#e5e7eb',
        chartGridColor: '#d1d5db',
        chartTextColor: '#6b7280',
        scrollbarTrack: '#f3f4f6',
        scrollbarThumb: '#9ca3af',
        scrollbarHover: '#6b7280'
      }));
    }
  };

  // Filter color groups based on search
  const filterColorGroups = (groups: ColorGroup[], search: string): ColorGroup[] => {
    if (!search.trim()) return groups;

    const searchLower = search.toLowerCase();
    return groups
      .map((group) => {
        const filteredColors = group.colors.filter(
          (color) =>
            color.label.toLowerCase().includes(searchLower) ||
            color.description.toLowerCase().includes(searchLower) ||
            color.affects.some((affect) => affect.toLowerCase().includes(searchLower)) ||
            color.key.toLowerCase().includes(searchLower)
        );

        // If group name matches, show all colors in that group
        if (
          group.name.toLowerCase().includes(searchLower) ||
          group.description.toLowerCase().includes(searchLower)
        ) {
          return group;
        }

        // Otherwise only show groups with matching colors
        return { ...group, colors: filteredColors };
      })
      .filter((group) => group.colors.length > 0);
  };

  // Filter colors by page
  const filterByPage = (groups: ColorGroup[], page: string): ColorGroup[] => {
    if (page === 'all') return groups;

    return groups
      .map((group) => {
        const filteredColors = group.colors.filter((color) => color.pages?.includes(page));
        return { ...group, colors: filteredColors };
      })
      .filter((group) => group.colors.length > 0);
  };

  // Get filtered groups based on organization mode
  const getFilteredGroups = useCallback(
    (groups: ColorGroup[], search: string): ColorGroup[] => {
      let filtered = groups;

      // Apply page filter if in page mode
      if (organizationMode === 'page') {
        filtered = filterByPage(filtered, selectedPage);
      }

      // Apply search filter
      if (search.trim()) {
        filtered = filterColorGroups(filtered, search);
      }

      return filtered;
    },
    [organizationMode, selectedPage]
  );

  const handleClose = () => {
    onClose();
  };

  return (
    <Modal opened={opened} onClose={handleClose} title="Create Custom Theme" size="xl">
      <div className="space-y-6">
        {/* Theme Metadata */}
        <div className="space-y-4">
          <h4 className="text-sm font-semibold flex items-center gap-2 text-themed-primary">
            <Info className="w-4 h-4" />
            Theme Information
          </h4>
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium mb-1 text-themed-secondary">
                Theme Name *
              </label>
              <input
                type="text"
                value={newTheme.name}
                onChange={(e) => setNewTheme({ ...newTheme, name: e.target.value })}
                placeholder="My Custom Theme"
                className="w-full px-3 py-2 focus:outline-none themed-input"
              />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1 text-themed-secondary">Author</label>
              <input
                type="text"
                value={newTheme.author}
                onChange={(e) => setNewTheme({ ...newTheme, author: e.target.value })}
                placeholder="Your Name"
                className="w-full px-3 py-2 focus:outline-none themed-input"
              />
            </div>
          </div>
          <div>
            <label className="block text-sm font-medium mb-1 text-themed-secondary">
              Description
            </label>
            <input
              type="text"
              value={newTheme.description}
              onChange={(e) => setNewTheme({ ...newTheme, description: e.target.value })}
              placeholder="A beautiful custom theme"
              className="w-full px-3 py-2 rounded focus:outline-none themed-input"
            />
          </div>
          <div className="space-y-3">
            <div className="flex items-center gap-4">
              <Checkbox
                checked={newTheme.isDark}
                onChange={(e) => loadPresetColors(e.target.checked ? 'dark' : 'light')}
                variant="rounded"
                label="Dark Theme"
              />
              <button
                onClick={() => loadPresetColors('dark')}
                className="px-3 py-1 text-xs rounded-lg flex items-center gap-1 bg-themed-tertiary text-themed-secondary"
              >
                <Moon className="w-3 h-3" />
                Load Dark Preset
              </button>
              <button
                onClick={() => loadPresetColors('light')}
                className="px-3 py-1 text-xs rounded-lg flex items-center gap-1 bg-themed-tertiary text-themed-secondary"
              >
                <Sun className="w-3 h-3" />
                Load Light Preset
              </button>
            </div>
          </div>
        </div>

        {/* Organization Mode Toggle */}
        <div className="flex gap-2">
          <button
            onClick={() => setOrganizationMode('category')}
            className={`flex-1 px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 ${
              organizationMode === 'category'
                ? 'bg-primary text-themed-button'
                : 'bg-themed-tertiary text-themed-secondary hover:bg-themed-hover'
            }`}
          >
            <Layers className="w-4 h-4 inline-block mr-2" />
            By Category
          </button>
          <button
            onClick={() => setOrganizationMode('page')}
            className={`flex-1 px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 ${
              organizationMode === 'page'
                ? 'bg-primary text-themed-button'
                : 'bg-themed-tertiary text-themed-secondary hover:bg-themed-hover'
            }`}
          >
            <Layout className="w-4 h-4 inline-block mr-2" />
            By Page
          </button>
        </div>

        {/* Page Selector (when in page mode) */}
        <div
          className={`transition-all duration-300 overflow-hidden ${
            organizationMode === 'page' ? 'max-h-40 opacity-100 mt-4' : 'max-h-0 opacity-0'
          }`}
        >
          <label className="block text-sm font-medium text-themed-primary mb-2">Select Page</label>
          <div className="grid grid-cols-3 gap-2">
            {pageDefinitions.map((page) => {
              const Icon = page.icon;
              return (
                <button
                  key={page.name}
                  onClick={() => setSelectedPage(page.name)}
                  className={`px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 flex items-center gap-2 ${
                    selectedPage === page.name
                      ? 'bg-primary'
                      : 'bg-themed-tertiary text-themed-secondary hover:bg-themed-hover'
                  }`}
                  style={{
                    color: selectedPage === page.name ? 'var(--theme-button-text)' : undefined
                  }}
                  title={page.description}
                >
                  <Icon className="w-4 h-4" />
                  {page.label}
                </button>
              );
            })}
          </div>
        </div>

        {/* Search Bar */}
        <div className="relative mt-4">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-themed-muted" />
          <input
            type="text"
            value={createSearchQuery}
            onChange={(e) => setCreateSearchQuery(e.target.value)}
            placeholder="Search colors... (e.g., 'alert', 'header', 'button', 'background')"
            className="w-full pl-10 pr-10 py-2 themed-input"
          />
          {createSearchQuery && (
            <button
              onClick={() => setCreateSearchQuery('')}
              className="absolute right-3 top-1/2 -translate-y-1/2 text-themed-muted hover:text-themed-primary"
            >
              <X className="w-4 h-4" />
            </button>
          )}
        </div>

        {/* Color Groups */}
        <div className="space-y-4 max-h-96 overflow-y-auto">
          {getFilteredGroups(colorGroups, createSearchQuery).map((group) => {
            const Icon = group.icon;
            const isExpanded =
              expandedGroups.includes(group.name) || createSearchQuery.trim() !== '';

            return (
              <div
                key={group.name}
                className="themed-card rounded-lg overflow-hidden"
                style={{ border: '1px solid var(--theme-border)' }}
              >
                <button
                  onClick={() => toggleGroup(group.name)}
                  className={`w-full px-4 py-3 flex items-center justify-between hover:bg-opacity-50 transition-all duration-200 ${
                    isExpanded ? 'rounded-t-lg' : 'rounded-lg'
                  }`}
                  style={{
                    backgroundColor: isExpanded ? 'var(--theme-bg-tertiary)' : 'transparent'
                  }}
                >
                  <div className="flex items-center gap-3">
                    <Icon className="w-4 h-4 text-themed-accent" />
                    <div className="text-left">
                      <h5 className="text-sm font-semibold capitalize text-themed-primary">
                        {group.name.replace(/([A-Z])/g, ' $1').trim()}
                      </h5>
                      <p className="text-xs text-themed-muted">{group.description}</p>
                    </div>
                  </div>
                  {isExpanded ? (
                    <ChevronDown className="w-4 h-4" />
                  ) : (
                    <ChevronRight className="w-4 h-4" />
                  )}
                </button>

                {isExpanded && (
                  <div
                    className="p-4 space-y-4 animate-expandDown rounded-b-lg"
                    style={{
                      backgroundColor: 'var(--theme-card-bg)',
                      borderTop: '1px solid var(--theme-border)',
                      animation: 'expandDown 0.3s ease-out'
                    }}
                  >
                    {group.colors.map((color) => (
                      <ImprovedColorPicker
                        key={color.key}
                        label={color.label}
                        description={color.description}
                        affects={color.affects}
                        value={newTheme[color.key] || '#ffffff'}
                        onChange={(value) => handleColorChange(color.key, value)}
                        onColorCommit={(previousColor) =>
                          handleColorCommit(color.key, previousColor)
                        }
                        supportsAlpha={color.supportsAlpha}
                        copiedColor={copiedColor}
                        onCopy={copyColor}
                        onRestore={() => restoreCreatePreviousColor(color.key)}
                        hasHistory={!!getCreateColorHistory(color.key)}
                      />
                    ))}
                  </div>
                )}
              </div>
            );
          })}
        </div>

        {/* Custom CSS */}
        <div>
          <label className="block text-sm font-medium mb-1 text-themed-secondary">
            Custom CSS (Optional)
          </label>
          <textarea
            value={newTheme.customCSS}
            onChange={(e) => setNewTheme({ ...newTheme, customCSS: e.target.value })}
            placeholder="/* Add any custom CSS here */"
            rows={4}
            className="w-full px-3 py-2 rounded font-mono text-xs focus:outline-none themed-input"
          />
        </div>

        {/* Actions */}
        <div
          className="flex justify-end space-x-3 pt-4 border-t"
          style={{ borderColor: 'var(--theme-border-primary)' }}
        >
          <Button variant="default" onClick={handleClose}>
            Cancel
          </Button>
          <Button
            variant="filled"
            leftSection={<Save className="w-4 h-4" />}
            onClick={onSave}
            disabled={!newTheme.name || !isAuthenticated || loading}
            className="themed-button-primary"
          >
            Create Theme
          </Button>
        </div>
      </div>
    </Modal>
  );
};

export default CreateThemeModal;

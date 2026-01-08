import React, { useState, useCallback } from 'react';
import { Info, Layers, Layout, Search, X, ChevronDown, ChevronRight, Save } from 'lucide-react';
import { Modal } from '../../ui/Modal';
import { Button } from '../../ui/Button';
import { Checkbox } from '../../ui/Checkbox';
import { ImprovedColorPicker } from '../../features/management/theme/ImprovedColorPicker';
import { colorGroups, pageDefinitions } from '../../features/management/theme/constants';
import { type ColorGroup, type Theme, type EditableTheme } from '../../features/management/theme/types';
import { storage } from '@utils/storage';

interface EditThemeModalProps {
  opened: boolean;
  onClose: () => void;
  onSave: () => void;
  isAuthenticated: boolean;
  editingTheme: Theme | null;
  editedTheme: EditableTheme;
  setEditedTheme: React.Dispatch<React.SetStateAction<EditableTheme>>;
  editOrganizationMode: 'category' | 'page';
  setEditOrganizationMode: React.Dispatch<React.SetStateAction<'category' | 'page'>>;
  editSelectedPage: string;
  setEditSelectedPage: React.Dispatch<React.SetStateAction<string>>;
  expandedGroups: string[];
  toggleGroup: (groupName: string) => void;
  copiedColor: string | null;
  copyColor: (color: string) => void;
  loading: boolean;
}

const EditThemeModal: React.FC<EditThemeModalProps> = ({
  opened,
  onClose,
  onSave,
  isAuthenticated,
  editingTheme,
  editedTheme,
  setEditedTheme,
  editOrganizationMode,
  setEditOrganizationMode,
  editSelectedPage,
  setEditSelectedPage,
  expandedGroups,
  toggleGroup,
  copiedColor,
  copyColor,
  loading
}) => {
  const [editSearchQuery, setEditSearchQuery] = useState('');

  const handleEditColorCommit = (key: string, previousColor: string) => {
    // Save the previous color to history when user finishes editing
    const historyKey = `color_history_${editingTheme?.meta.id}_${key}`;
    const originalKey = `color_history_${editingTheme?.meta.id}_${key}_original`;
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

  const handleEditColorChange = (key: string, value: string) => {
    // Just update the value, don't save to history on every change
    setEditedTheme((prev) => ({ ...prev, [key]: value }));
  };

  const restorePreviousColor = (key: string) => {
    const historyKey = `color_history_${editingTheme?.meta.id}_${key}`;
    const originalKey = `color_history_${editingTheme?.meta.id}_${key}_original`;
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
          setEditedTheme((prev) => ({ ...prev, [key]: previousColor }));
        }
      }
    } else {
      // No recent history, restore to original color and remove it (final undo)
      const originalColor = storage.getItem(originalKey);
      if (originalColor) {
        setEditedTheme((prev) => ({ ...prev, [key]: originalColor }));
        // Remove original after restoring to it (no more undos available)
        storage.removeItem(originalKey);
      }
    }
  };

  const getEditColorHistory = (key: string) => {
    const historyKey = `color_history_${editingTheme?.meta.id}_${key}`;
    const originalKey = `color_history_${editingTheme?.meta.id}_${key}_original`;
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
      if (editOrganizationMode === 'page') {
        filtered = filterByPage(filtered, editSelectedPage);
      }

      // Apply search filter
      if (search.trim()) {
        filtered = filterColorGroups(filtered, search);
      }

      return filtered;
    },
    [editOrganizationMode, editSelectedPage]
  );

  const handleClose = () => {
    onClose();
  };

  return (
    <Modal
      opened={opened}
      onClose={handleClose}
      title={`Edit Theme: ${editingTheme?.meta.name || ''}`}
      size="xl"
    >
      <div className="space-y-6">
        {/* Community Theme Notice */}
        {editingTheme?.meta.isCommunityTheme && (
          <div className="p-4 rounded-lg border bg-info border-info">
            <div className="flex items-start gap-3">
              <Info className="w-5 h-5 flex-shrink-0 mt-0.5 text-info" />
              <div className="flex-1">
                <p className="text-sm font-medium text-themed-primary mb-1">
                  Editing Community Theme
                </p>
                <p className="text-xs text-themed-muted">
                  This is a community theme that receives automatic updates. Your edits will create
                  a custom copy named "{editedTheme.name} (Custom)" that won't receive updates. The
                  original theme will be kept for future updates.
                </p>
              </div>
            </div>
          </div>
        )}

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
                value={editedTheme.name || ''}
                onChange={(e) => setEditedTheme({ ...editedTheme, name: e.target.value })}
                placeholder="My Custom Theme"
                className="w-full px-3 py-2 focus:outline-none themed-input"
              />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1 text-themed-secondary">Author</label>
              <input
                type="text"
                value={editedTheme.author || ''}
                onChange={(e) => setEditedTheme({ ...editedTheme, author: e.target.value })}
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
              value={editedTheme.description || ''}
              onChange={(e) => setEditedTheme({ ...editedTheme, description: e.target.value })}
              placeholder="A beautiful custom theme"
              className="w-full px-3 py-2 rounded focus:outline-none themed-input"
            />
          </div>
          <div className="space-y-3">
            <div className="flex items-center gap-4">
              <Checkbox
                checked={editedTheme.isDark || false}
                onChange={(e) => setEditedTheme({ ...editedTheme, isDark: e.target.checked })}
                variant="rounded"
                label="Dark Theme"
              />
              <span className="text-xs text-themed-muted">Theme ID: {editingTheme?.meta.id}</span>
            </div>
          </div>
        </div>

        {/* Organization Mode Toggle */}
        <div className="flex gap-2">
          <button
            onClick={() => setEditOrganizationMode('category')}
            className={`flex-1 px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 ${
              editOrganizationMode === 'category'
                ? 'bg-primary text-themed-button'
                : 'bg-themed-tertiary text-themed-secondary hover:bg-themed-hover'
            }`}
          >
            <Layers className="w-4 h-4 inline-block mr-2" />
            By Category
          </button>
          <button
            onClick={() => setEditOrganizationMode('page')}
            className={`flex-1 px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 ${
              editOrganizationMode === 'page'
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
            editOrganizationMode === 'page' ? 'max-h-40 opacity-100 mt-4' : 'max-h-0 opacity-0'
          }`}
        >
          <label className="block text-sm font-medium text-themed-primary mb-2">Select Page</label>
          <div className="grid grid-cols-3 gap-2">
            {pageDefinitions.map((page) => {
              const Icon = page.icon;
              return (
                <button
                  key={page.name}
                  onClick={() => setEditSelectedPage(page.name)}
                  className={`px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 flex items-center gap-2 ${
                    editSelectedPage === page.name
                      ? 'bg-primary'
                      : 'bg-themed-tertiary text-themed-secondary hover:bg-themed-hover'
                  }`}
                  style={{
                    color: editSelectedPage === page.name ? 'var(--theme-button-text)' : undefined
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
            value={editSearchQuery}
            onChange={(e) => setEditSearchQuery(e.target.value)}
            placeholder="Search colors... (e.g., 'alert', 'header', 'button', 'background')"
            className="w-full pl-10 pr-10 py-2 themed-input"
          />
          {editSearchQuery && (
            <button
              onClick={() => setEditSearchQuery('')}
              className="absolute right-3 top-1/2 -translate-y-1/2 text-themed-muted hover:text-themed-primary"
            >
              <X className="w-4 h-4" />
            </button>
          )}
        </div>

        {/* Color Groups */}
        <div className="space-y-4 max-h-96 overflow-y-auto">
          {getFilteredGroups(colorGroups, editSearchQuery).map((group) => {
            const Icon = group.icon;
            const isExpanded = expandedGroups.includes(group.name) || editSearchQuery.trim() !== '';

            return (
              <div
                key={group.name}
                className="themed-card rounded-lg overflow-hidden border border-themed"
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
                  <div className="p-4 space-y-4 animate-expandDown rounded-b-lg bg-themed-card border-t border-themed">
                    {group.colors.map((color) => (
                      <ImprovedColorPicker
                        key={color.key}
                        label={color.label}
                        description={color.description}
                        affects={color.affects}
                        value={(editedTheme[color.key] as string) || ''}
                        onChange={(value) => handleEditColorChange(color.key, value)}
                        onColorCommit={(previousColor) =>
                          handleEditColorCommit(color.key, previousColor)
                        }
                        supportsAlpha={color.supportsAlpha}
                        copiedColor={copiedColor}
                        onCopy={copyColor}
                        onRestore={() => restorePreviousColor(color.key)}
                        hasHistory={!!getEditColorHistory(color.key)}
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
            value={editedTheme.customCSS || ''}
            onChange={(e) => setEditedTheme({ ...editedTheme, customCSS: e.target.value })}
            placeholder="/* Add any custom CSS here */"
            rows={4}
            className="w-full px-3 py-2 rounded font-mono text-xs focus:outline-none themed-input"
          />
        </div>

        {/* Actions */}
        <div className="flex justify-end space-x-3 pt-4 border-t border-themed-primary">
          <Button variant="default" onClick={handleClose}>
            Cancel
          </Button>
          <Button
            variant="filled"
            leftSection={<Save className="w-4 h-4" />}
            onClick={onSave}
            disabled={!editedTheme.name || !isAuthenticated || loading}
            className="themed-button-primary"
          >
            Save Changes
          </Button>
        </div>
      </div>
    </Modal>
  );
};

export default EditThemeModal;

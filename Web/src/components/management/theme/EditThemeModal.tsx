import React, { useState, useCallback } from 'react';
import {
  Info,
  Layers,
  Layout,
  Search,
  X,
  ChevronDown,
  ChevronRight,
  Check,
  Copy,
  RotateCcw,
  Percent,
  Save
} from 'lucide-react';
import { Modal } from '../../ui/Modal';
import { Button } from '../../ui/Button';
import { Checkbox } from '../../ui/Checkbox';
import { colorGroups, pageDefinitions } from './constants';
import { ColorGroup, Theme } from './types';

interface EditThemeModalProps {
  opened: boolean;
  onClose: () => void;
  onSave: () => void;
  isAuthenticated: boolean;
  editingTheme: Theme | null;
  editedTheme: any;
  setEditedTheme: React.Dispatch<React.SetStateAction<any>>;
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
  const [colorEditingStarted, setColorEditingStarted] = useState<Record<string, boolean>>({});

  // Helper functions
  const hexToRgba = (hex: string, alpha: number = 1): string => {
    const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex);
    if (!result) return hex;
    const r = parseInt(result[1], 16);
    const g = parseInt(result[2], 16);
    const b = parseInt(result[3], 16);
    return `rgba(${r}, ${g}, ${b}, ${alpha})`;
  };

  const parseColorValue = (color: string): { hex: string; alpha: number } => {
    // Handle rgba format
    const rgbaMatch = color.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)/);
    if (rgbaMatch) {
      const r = parseInt(rgbaMatch[1]);
      const g = parseInt(rgbaMatch[2]);
      const b = parseInt(rgbaMatch[3]);
      const alpha = rgbaMatch[4] ? parseFloat(rgbaMatch[4]) : 1;
      const hex = '#' + [r, g, b].map(x => x.toString(16).padStart(2, '0')).join('');
      return { hex, alpha };
    }
    // Handle hex format
    return { hex: color, alpha: 1 };
  };

  const updateColorWithAlpha = (key: string, hex: string, alpha: number) => {
    const colorValue = alpha < 1 ? hexToRgba(hex, alpha) : hex;
    setEditedTheme((prev: any) => ({ ...prev, [key]: colorValue }));
  };

  const handleEditColorStart = (key: string) => {
    // Save the original color when user starts editing (not on every change)
    if (!colorEditingStarted[key]) {
      const currentValue = editedTheme[key];
      if (currentValue && currentValue.match(/^#[0-9a-fA-F]{6}$/)) {
        const historyKey = `color_history_${editingTheme?.meta.id}_${key}`;
        localStorage.setItem(historyKey, currentValue);
      }
      setColorEditingStarted(prev => ({ ...prev, [key]: true }));
    }
  };

  const handleEditColorChange = (key: string, value: string) => {
    // Just update the value, don't save to history on every change
    setEditedTheme((prev: any) => ({ ...prev, [key]: value }));
  };

  const restorePreviousColor = (key: string) => {
    const historyKey = `color_history_${editingTheme?.meta.id}_${key}`;
    const previousColor = localStorage.getItem(historyKey);
    if (previousColor) {
      // Swap current with history
      const currentColor = editedTheme[key];
      setEditedTheme((prev: any) => ({ ...prev, [key]: previousColor }));
      localStorage.setItem(historyKey, currentColor || '');
    }
  };

  const getEditColorHistory = (key: string) => {
    const historyKey = `color_history_${editingTheme?.meta.id}_${key}`;
    const value = localStorage.getItem(historyKey);
    return value;
  };

  // Filter color groups based on search
  const filterColorGroups = (groups: ColorGroup[], search: string): ColorGroup[] => {
    if (!search.trim()) return groups;

    const searchLower = search.toLowerCase();
    return groups.map(group => {
      const filteredColors = group.colors.filter(color =>
        color.label.toLowerCase().includes(searchLower) ||
        color.description.toLowerCase().includes(searchLower) ||
        color.affects.some(affect => affect.toLowerCase().includes(searchLower)) ||
        color.key.toLowerCase().includes(searchLower)
      );

      // If group name matches, show all colors in that group
      if (group.name.toLowerCase().includes(searchLower) ||
          group.description.toLowerCase().includes(searchLower)) {
        return group;
      }

      // Otherwise only show groups with matching colors
      return { ...group, colors: filteredColors };
    }).filter(group => group.colors.length > 0);
  };

  // Filter colors by page
  const filterByPage = (groups: ColorGroup[], page: string): ColorGroup[] => {
    if (page === 'all') return groups;

    return groups.map(group => {
      const filteredColors = group.colors.filter(color =>
        color.pages?.includes(page)
      );
      return { ...group, colors: filteredColors };
    }).filter(group => group.colors.length > 0);
  };

  // Get filtered groups based on organization mode
  const getFilteredGroups = useCallback((groups: ColorGroup[], search: string): ColorGroup[] => {
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
  }, [editOrganizationMode, editSelectedPage]);

  const handleClose = () => {
    onClose();
    setColorEditingStarted({});
  };

  return (
    <Modal
      opened={opened}
      onClose={handleClose}
      title={`Edit Theme: ${editingTheme?.meta.name || ''}`}
      size="xl"
    >
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
                value={editedTheme.name || ''}
                onChange={(e) => setEditedTheme({ ...editedTheme, name: e.target.value })}
                placeholder="My Custom Theme"
                className="w-full px-3 py-2 focus:outline-none themed-input"
              />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1 text-themed-secondary">
                Author
              </label>
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
        <div className={`transition-all duration-300 overflow-hidden ${
          editOrganizationMode === 'page' ? 'max-h-40 opacity-100 mt-4' : 'max-h-0 opacity-0'
        }`}>
          <label className="block text-sm font-medium text-themed-primary mb-2">
            Select Page
          </label>
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
                    color: editSelectedPage === page.name
                      ? 'var(--theme-button-text)'
                      : undefined
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
                className="themed-card rounded-lg"
                style={{ border: '1px solid var(--theme-border)' }}
              >
                <button
                  onClick={() => toggleGroup(group.name)}
                  className="w-full px-4 py-3 flex items-center justify-between hover:bg-opacity-50 transition-all duration-200"
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
                    className="p-4 themed-card space-y-4 animate-expandDown"
                    style={{
                      borderTop: '1px solid var(--theme-border)',
                      animation: 'expandDown 0.3s ease-out'
                    }}
                  >
                    {group.colors.map((color) => (
                      <div key={color.key} className="space-y-2">
                        <div className="flex items-center justify-between">
                          <div className="flex-1">
                            <label className="block text-sm font-medium text-themed-primary">
                              {color.label}
                            </label>
                            <p className="text-xs text-themed-muted">{color.description}</p>
                            <div className="flex flex-wrap gap-1 mt-1">
                              {color.affects.map((item, idx) => (
                                <span
                                  key={idx}
                                  className="text-xs px-1.5 py-0.5 rounded"
                                  style={{
                                    backgroundColor: 'var(--theme-bg-hover)',
                                    color: 'var(--theme-text-secondary)'
                                  }}
                                >
                                  {item}
                                </span>
                              ))}
                            </div>
                          </div>
                          <div className="flex items-center gap-2">
                            {(() => {
                              const { hex, alpha } = parseColorValue(editedTheme[color.key] || '#000000');
                              return (
                                <>
                                  <div className="relative">
                                    <input
                                      type="color"
                                      value={hex}
                                      onMouseDown={() => handleEditColorStart(color.key)}
                                      onFocus={() => handleEditColorStart(color.key)}
                                      onChange={(e) => {
                                        const currentAlpha = parseColorValue(editedTheme[color.key] || '#000000').alpha;
                                        updateColorWithAlpha(color.key, e.target.value, currentAlpha);
                                      }}
                                      className="w-12 h-8 rounded cursor-pointer"
                                      style={{ backgroundColor: editedTheme[color.key] || '#000000' }}
                                    />
                                  </div>
                                  {color.supportsAlpha && (
                                    <div className="flex items-center gap-1">
                                      <Percent className="w-3 h-3 text-themed-muted" />
                                      <input
                                        type="range"
                                        min="0"
                                        max="100"
                                        value={Math.round(alpha * 100)}
                                        onChange={(e) => {
                                          const newAlpha = parseInt(e.target.value) / 100;
                                          updateColorWithAlpha(color.key, hex, newAlpha);
                                        }}
                                        className="w-16"
                                        title={`Opacity: ${Math.round(alpha * 100)}%`}
                                      />
                                      <span className="text-xs text-themed-muted w-8">
                                        {Math.round(alpha * 100)}%
                                      </span>
                                    </div>
                                  )}
                                  <input
                                    type="text"
                                    value={editedTheme[color.key] || ''}
                                    onFocus={() => handleEditColorStart(color.key)}
                                    onChange={(e) => handleEditColorChange(color.key, e.target.value)}
                                    className="w-24 px-2 py-1 text-xs rounded font-mono themed-input"
                                    placeholder={color.key}
                                  />
                                  <button
                                    onClick={() => copyColor(editedTheme[color.key] || '')}
                                    className="p-1 rounded-lg hover:bg-opacity-50 bg-themed-hover"
                                    title="Copy color"
                                  >
                                    {copiedColor === editedTheme[color.key] ? (
                                      <Check
                                        className="w-3 h-3"
                                        style={{ color: 'var(--theme-success)' }}
                                      />
                                    ) : (
                                      <Copy className="w-3 h-3 text-themed-muted" />
                                    )}
                                  </button>
                                  {(() => {
                                    const historyColor = getEditColorHistory(color.key);
                                    if (!historyColor) return null;

                                    return (
                                      <button
                                        onClick={() => restorePreviousColor(color.key)}
                                        className="p-1 rounded-lg hover:bg-opacity-50 bg-themed-hover"
                                        title={`Restore previous color: ${historyColor}`}
                                      >
                                        <RotateCcw className="w-3 h-3 text-themed-muted" />
                                      </button>
                                    );
                                  })()}
                                  <button
                                    onClick={() => {
                                      setEditedTheme((prev: any) => {
                                        const updated = { ...prev };
                                        delete updated[color.key];
                                        return updated;
                                      });
                                    }}
                                    className="p-1 rounded-lg hover:bg-opacity-50 bg-themed-hover"
                                    title="Reset to default"
                                  >
                                    <X className="w-3 h-3 text-themed-warning" />
                                  </button>
                                </>
                              );
                            })()}
                          </div>
                        </div>
                      </div>
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
        <div
          className="flex justify-end space-x-3 pt-4 border-t"
          style={{ borderColor: 'var(--theme-border-primary)' }}
        >
          <Button
            variant="default"
            onClick={handleClose}
          >
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

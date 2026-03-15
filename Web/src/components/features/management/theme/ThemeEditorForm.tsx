import React, { useState, useCallback } from 'react';
import { Layers, Layout, Search, X, ChevronDown, ChevronRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { ImprovedColorPicker } from './ImprovedColorPicker';
import { colorGroups, pageDefinitions } from './constants';
import { type ColorGroup } from './types';

interface ThemeEditorFormProps {
  themeData: Record<string, string | boolean>;
  onColorChange: (key: string, value: string) => void;
  onMetaChange: (key: string, value: string | boolean) => void;
  colorHistory: {
    commitColor: (key: string, previousColor: string) => void;
    restoreColor: (key: string, applyColor: (color: string) => void) => void;
    hasHistory: (key: string) => boolean;
  };
}

const ThemeEditorForm: React.FC<ThemeEditorFormProps> = ({
  themeData,
  onColorChange,
  onMetaChange,
  colorHistory
}) => {
  const { t } = useTranslation();
  const [organizationMode, setOrganizationMode] = useState<'category' | 'page'>('category');
  const [selectedPage, setSelectedPage] = useState('all');
  const [expandedGroups, setExpandedGroups] = useState<string[]>(['foundation']);
  const [copiedColor, setCopiedColor] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');

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

  // Translation helpers
  const getGroupTitle = (group: ColorGroup) => t(`modals.theme.groups.${group.name}.title`);
  const getGroupDescription = (group: ColorGroup) =>
    t(`modals.theme.groups.${group.name}.description`);
  const getColorLabel = (color: ColorGroup['colors'][number]) =>
    t(`modals.theme.colors.${color.key}.label`);
  const getColorDescription = (color: ColorGroup['colors'][number]) =>
    t(`modals.theme.colors.${color.key}.description`);
  const getColorAffects = (color: ColorGroup['colors'][number]) => {
    const translatedAffects = t(`modals.theme.colors.${color.key}.affects`, {
      returnObjects: true
    });
    if (Array.isArray(translatedAffects)) {
      return translatedAffects as string[];
    }
    return [];
  };
  const getPageLabel = (page: (typeof pageDefinitions)[number]) =>
    t(`modals.theme.pages.${page.name}.label`);
  const getPageDescription = (page: (typeof pageDefinitions)[number]) =>
    t(`modals.theme.pages.${page.name}.description`);

  // Filter color groups based on search
  const filterColorGroups = (groups: ColorGroup[], search: string): ColorGroup[] => {
    if (!search.trim()) return groups;

    const searchLower = search.toLowerCase();
    return groups
      .map((group) => {
        const filteredColors = group.colors.filter(
          (color) =>
            getColorLabel(color).toLowerCase().includes(searchLower) ||
            getColorDescription(color).toLowerCase().includes(searchLower) ||
            getColorAffects(color).some((affect) => affect.toLowerCase().includes(searchLower)) ||
            color.key.toLowerCase().includes(searchLower)
        );

        // If group name matches, show all colors in that group
        if (
          getGroupTitle(group).toLowerCase().includes(searchLower) ||
          getGroupDescription(group).toLowerCase().includes(searchLower)
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [organizationMode, selectedPage]
  );

  return (
    <>
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
          {t('modals.theme.organization.byCategory')}
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
          {t('modals.theme.organization.byPage')}
        </button>
      </div>

      {/* Page Selector (when in page mode) */}
      <div
        className={`transition-all duration-300 overflow-hidden ${
          organizationMode === 'page' ? 'max-h-40 opacity-100 mt-4' : 'max-h-0 opacity-0'
        }`}
      >
        <label className="block text-sm font-medium text-themed-primary mb-2">
          {t('modals.theme.organization.selectPage')}
        </label>
        <div className="grid grid-cols-3 gap-2">
          {pageDefinitions.map((page) => {
            const Icon = page.icon;
            return (
              <button
                key={page.name}
                onClick={() => setSelectedPage(page.name)}
                className={`px-3 py-2 rounded-lg text-sm font-medium transition-all duration-200 flex items-center gap-2 ${
                  selectedPage === page.name
                    ? 'bg-primary text-themed-button'
                    : 'bg-themed-tertiary text-themed-secondary hover:bg-themed-hover'
                }`}
                title={getPageDescription(page)}
              >
                <Icon className="w-4 h-4" />
                {getPageLabel(page)}
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
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
          placeholder={t('modals.theme.placeholders.searchColors')}
          className="w-full pl-10 pr-10 py-2 themed-input"
        />
        {searchQuery && (
          <button
            onClick={() => setSearchQuery('')}
            className="absolute right-3 top-1/2 -translate-y-1/2 text-themed-muted hover:text-themed-primary"
          >
            <X className="w-4 h-4" />
          </button>
        )}
      </div>

      {/* Color Groups */}
      <div className="space-y-4 max-h-96 overflow-y-auto">
        {getFilteredGroups(colorGroups, searchQuery).map((group) => {
          const Icon = group.icon;
          const isExpanded = expandedGroups.includes(group.name) || searchQuery.trim() !== '';

          return (
            <div
              key={group.name}
              className="themed-card rounded-lg overflow-hidden border border-themed"
            >
              <button
                onClick={() => toggleGroup(group.name)}
                className={`w-full px-4 py-3 flex items-center justify-between hover:bg-opacity-50 transition-all duration-200 ${
                  isExpanded ? 'rounded-t-lg bg-themed-tertiary' : 'rounded-lg bg-transparent'
                }`}
              >
                <div className="flex items-center gap-3">
                  <Icon className="w-4 h-4 text-themed-accent" />
                  <div className="text-left">
                    <h5 className="text-sm font-semibold capitalize text-themed-primary">
                      {getGroupTitle(group)}
                    </h5>
                    <p className="text-xs text-themed-muted">{getGroupDescription(group)}</p>
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
                      label={getColorLabel(color)}
                      description={getColorDescription(color)}
                      affects={getColorAffects(color)}
                      value={(themeData[color.key] as string) || ''}
                      onChange={(value) => onColorChange(color.key, value)}
                      onColorCommit={(previousColor) =>
                        colorHistory.commitColor(color.key, previousColor)
                      }
                      supportsAlpha={color.supportsAlpha}
                      copiedColor={copiedColor}
                      onCopy={copyColor}
                      onRestore={() =>
                        colorHistory.restoreColor(color.key, (restoredColor) =>
                          onColorChange(color.key, restoredColor)
                        )
                      }
                      hasHistory={colorHistory.hasHistory(color.key)}
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
          {t('modals.theme.form.customCss')}
        </label>
        <textarea
          value={(themeData.customCSS as string) || ''}
          onChange={(e) => onMetaChange('customCSS', e.target.value)}
          placeholder={t('modals.theme.placeholders.customCss')}
          rows={4}
          className="w-full px-3 py-2 rounded font-mono text-xs focus:outline-none themed-input"
        />
      </div>
    </>
  );
};

export default ThemeEditorForm;

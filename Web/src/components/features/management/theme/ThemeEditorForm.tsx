import React, { useState } from 'react';
import { SearchX, type LucideIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { AccordionSection } from '@components/ui/AccordionSection';
import { AccordionGroupProvider } from '@components/ui/AccordionGroupProvider';
import { AccordionGroupToggle } from '@components/ui/AccordionGroupToggle';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { SearchInput } from '@components/ui/SearchInput';
import { Button } from '@components/ui/Button';
import { EmptyState } from '@components/ui/ManagerCard';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { ImprovedColorPicker } from './ImprovedColorPicker';
import { colorGroups, pageDefinitions } from './constants';
import { type ColorGroup } from './types';
import { copyText } from '@utils/clipboard';
import { useCopyFeedback } from '@/hooks/useCopyFeedback';
import '@/styles/features/theme-editor-form.css';

interface ThemeEditorFormProps {
  themeData: Record<string, string | boolean>;
  onColorChange: (key: string, value: string) => void;
  /**
   * Optional because the only field this form ever used it for was the custom CSS box, which
   * now lives on its own pane. Kept in the shape so a caller still handing it down does not
   * have to change.
   */
  onMetaChange?: (key: string, value: string | boolean) => void;
  colorHistory: {
    commitColor: (key: string, previousColor: string) => void;
    restoreColor: (key: string, applyColor: (color: string) => void) => void;
    hasHistory: (key: string) => boolean;
  };
}

interface ColorGroupSectionProps {
  group: ColorGroup;
  title: string;
  description: string;
  isExpanded: boolean;
  onToggle: () => void;
  children: React.ReactNode;
}

/**
 * One collapsible group of color fields.
 *
 * A component of its own rather than markup inside the list's map, because it has to call
 * `useAccordionGroupItem` to reach the expand-all control, and the number of groups on
 * screen changes with the search and the page filter, so a hook cannot run in that loop.
 */
const ColorGroupSection: React.FC<ColorGroupSectionProps> = ({
  group,
  title,
  description,
  isExpanded,
  onToggle,
  children
}) => {
  useAccordionGroupItem(`theme-colors-${group.name}`, isExpanded, onToggle);

  return (
    <AccordionSection
      title={title}
      // The shared header takes an icon component that accepts size/className/style, while
      // the group list types the same lucide icons as the wider ElementType.
      icon={group.icon as LucideIcon}
      count={group.colors.length}
      isExpanded={isExpanded}
      onToggle={onToggle}
    >
      <p className="text-xs text-themed-muted mb-3">{description}</p>
      <div className="space-y-4">{children}</div>
    </AccordionSection>
  );
};

const ThemeEditorForm: React.FC<ThemeEditorFormProps> = ({
  themeData,
  onColorChange,
  colorHistory
}) => {
  const { t } = useTranslation();
  const [selectedPage, setSelectedPage] = useState('all');
  const [expandedGroups, setExpandedGroups] = useState<string[]>(['foundation']);
  const [copiedColor, markCopied] = useCopyFeedback<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');

  const toggleGroup = (groupName: string) => {
    setExpandedGroups((prev) =>
      prev.includes(groupName) ? prev.filter((g) => g !== groupName) : [...prev, groupName]
    );
  };

  const copyColor = async (color: string) => {
    // Only claims the copy when it happened. The bare clipboard call this replaced threw on a page
    // served over plain http, where the API does not exist, and still showed the copied tick.
    if (await copyText(color)) {
      markCopied(color);
    }
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

  const pageOptions: DropdownOption[] = pageDefinitions.map((page) => ({
    value: page.name,
    label: getPageLabel(page),
    description: getPageDescription(page)
  }));

  const visibleGroups = filterColorGroups(filterByPage(colorGroups, selectedPage), searchQuery);

  return (
    <AccordionGroupProvider>
      <div className="cluster mb-4">
        <div className="theme-editor-form__search">
          <SearchInput
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            placeholder={t('modals.theme.placeholders.searchColors')}
            onClear={() => setSearchQuery('')}
          />
        </div>

        <EnhancedDropdown
          options={pageOptions}
          value={selectedPage}
          onChange={setSelectedPage}
          size="md"
          prefix={t('modals.theme.organization.selectPage')}
          triggerAriaLabel={t('modals.theme.organization.selectPage')}
          className="theme-editor-form__page-filter"
        />

        <span className="theme-editor-form__expand-all">
          <AccordionGroupToggle />
        </span>
      </div>

      {visibleGroups.length === 0 ? (
        // A search that matches nothing used to render an empty list and no words at all.
        <EmptyState
          variant="panel"
          icon={SearchX}
          title={t('ui.dropdown.noMatches')}
          subtitle={searchQuery}
          action={
            <Button type="button" variant="default" size="sm" onClick={() => setSearchQuery('')}>
              {t('common.clearSearch')}
            </Button>
          }
        />
      ) : (
        <div className="space-y-4">
          {visibleGroups.map((group) => (
            <ColorGroupSection
              key={group.name}
              group={group}
              title={getGroupTitle(group)}
              description={getGroupDescription(group)}
              isExpanded={expandedGroups.includes(group.name) || searchQuery.trim() !== ''}
              onToggle={() => toggleGroup(group.name)}
            >
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
            </ColorGroupSection>
          ))}
        </div>
      )}
    </AccordionGroupProvider>
  );
};

export default ThemeEditorForm;

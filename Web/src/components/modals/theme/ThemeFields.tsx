import { noAutofill } from '@utils/autofill';
import { useTranslation } from 'react-i18next';
import { Checkbox } from '@components/ui/Checkbox';
import FormField from '@components/ui/FormField';
import { ImprovedColorPicker } from '@components/features/management/theme/ImprovedColorPicker';
import { type EditableTheme } from '@components/features/management/theme/types';
import { useCopyFeedback } from '@hooks/useCopyFeedback';
import { type useColorHistory } from '@hooks/useColorHistory';
import { copyText } from '@utils/clipboard';
import { THEME_BASE_COLORS } from './constants';
import type { ReactNode, ChangeEvent } from 'react';

interface ThemeFieldsProps {
  name: string;
  author: string;
  description: string;
  isDark: boolean;
  onNameChange: (value: string) => void;
  onAuthorChange: (value: string) => void;
  onDescriptionChange: (value: string) => void;
  /** Dark-theme checkbox handler. Create repaints the whole form from a preset; Edit just flips the flag. */
  onDarkChange: (checked: boolean) => void;
  /** Content in the dark-theme row: Create's preset-load pair (its own full-width line), Edit's theme-id note. */
  trailingContent: ReactNode;
  /** The whole draft, so the base color rows can read their current values. */
  themeData: EditableTheme;
  onColorChange: (key: string, value: string) => void;
  colorHistory: ReturnType<typeof useColorHistory>;
}

/**
 * The Basics pane of the create and edit theme modals: the theme's name, author, description and
 * dark/light flag, then the handful of colors every other token is derived from.
 */
export const ThemeFields: React.FC<ThemeFieldsProps> = ({
  name,
  author,
  description,
  isDark,
  onNameChange,
  onAuthorChange,
  onDescriptionChange,
  onDarkChange,
  trailingContent,
  themeData,
  onColorChange,
  colorHistory
}) => {
  const { t } = useTranslation();
  const [copiedColor, markCopied] = useCopyFeedback<string | null>(null);

  const copyColor = async (color: string) => {
    // Only claims the copy when it happened; over plain http the clipboard API is absent.
    if (await copyText(color)) {
      markCopied(color);
    }
  };

  const colorAffects = (key: string): string[] => {
    const translated = t(`modals.theme.colors.${key}.affects`, { returnObjects: true });
    return Array.isArray(translated) ? (translated as string[]) : [];
  };

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-4">
        <div>
          <FormField label={t('modals.theme.form.themeName')}>
            {(field) => (
              <input
                {...noAutofill}
                {...field}
                type="text"
                value={name}
                onChange={(e: ChangeEvent<HTMLInputElement>) => onNameChange(e.target.value)}
                placeholder={t('modals.theme.placeholders.themeName')}
                className="w-full px-3 py-2 text-sm control-h-md focus:outline-none themed-input"
              />
            )}
          </FormField>
        </div>
        <div>
          <FormField label={t('modals.theme.form.author')}>
            {(field) => (
              <input
                {...noAutofill}
                {...field}
                type="text"
                value={author}
                onChange={(e: ChangeEvent<HTMLInputElement>) => onAuthorChange(e.target.value)}
                placeholder={t('modals.theme.placeholders.author')}
                className="w-full px-3 py-2 text-sm control-h-md focus:outline-none themed-input"
              />
            )}
          </FormField>
        </div>
      </div>
      <div>
        <FormField label={t('modals.theme.form.description')}>
          {(field) => (
            <input
              {...noAutofill}
              {...field}
              type="text"
              value={description}
              onChange={(e: ChangeEvent<HTMLInputElement>) => onDescriptionChange(e.target.value)}
              placeholder={t('modals.theme.placeholders.description')}
              className="w-full px-3 py-2 text-sm control-h-md focus:outline-none themed-input"
            />
          )}
        </FormField>
      </div>
      {/* Wraps rather than squeezing: the preset buttons do not shrink, and on a phone they used to
          compress the checkbox out of square. */}
      <div className="flex flex-wrap items-center gap-4">
        <Checkbox
          checked={isDark}
          onChange={(e: ChangeEvent<HTMLInputElement>) => onDarkChange(e.target.checked)}
          variant="rounded"
          className="shrink-0"
          label={t('modals.theme.form.darkTheme')}
        />
        {trailingContent}
      </div>
      <div className="theme-editor-modal__base-colors">
        {THEME_BASE_COLORS.map((color) => (
          <ImprovedColorPicker
            key={color.key}
            label={t(`modals.theme.colors.${color.key}.label`)}
            description={t(`modals.theme.colors.${color.key}.description`)}
            affects={colorAffects(color.key)}
            value={(themeData[color.key] as string) || ''}
            onChange={(value) => onColorChange(color.key, value)}
            onColorCommit={(previousColor) => colorHistory.commitColor(color.key, previousColor)}
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
    </div>
  );
};

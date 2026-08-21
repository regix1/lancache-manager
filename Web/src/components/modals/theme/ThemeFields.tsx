import { Info } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Checkbox } from '@components/ui/Checkbox';
import FormField from '@components/ui/FormField';
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
  /** Content beside the dark-theme checkbox: Create's two preset-load buttons, Edit's theme-id note. */
  trailingContent: ReactNode;
}

/**
 * Name / author / description / dark-theme fields shared by the create and edit theme modals.
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
  trailingContent
}) => {
  const { t } = useTranslation();

  return (
    <div className="space-y-4">
      <h4 className="text-sm font-semibold flex items-center gap-2 text-themed-primary">
        <Info className="w-4 h-4" />
        {t('modals.theme.form.themeInfo')}
      </h4>
      <div className="grid grid-cols-2 gap-4">
        <div>
          <FormField label={t('modals.theme.form.themeName')}>
            {(field) => (
              <input
                {...field}
                type="text"
                value={name}
                onChange={(e: ChangeEvent<HTMLInputElement>) => onNameChange(e.target.value)}
                placeholder={t('modals.theme.placeholders.themeName')}
                className="w-full px-3 py-2 focus:outline-none themed-input"
              />
            )}
          </FormField>
        </div>
        <div>
          <FormField label={t('modals.theme.form.author')}>
            {(field) => (
              <input
                {...field}
                type="text"
                value={author}
                onChange={(e: ChangeEvent<HTMLInputElement>) => onAuthorChange(e.target.value)}
                placeholder={t('modals.theme.placeholders.author')}
                className="w-full px-3 py-2 focus:outline-none themed-input"
              />
            )}
          </FormField>
        </div>
      </div>
      <div>
        <FormField label={t('modals.theme.form.description')}>
          {(field) => (
            <input
              {...field}
              type="text"
              value={description}
              onChange={(e: ChangeEvent<HTMLInputElement>) => onDescriptionChange(e.target.value)}
              placeholder={t('modals.theme.placeholders.description')}
              className="w-full px-3 py-2 rounded focus:outline-none themed-input"
            />
          )}
        </FormField>
      </div>
      <div className="space-y-3">
        <div className="flex items-center gap-4">
          <Checkbox
            checked={isDark}
            onChange={(e: ChangeEvent<HTMLInputElement>) => onDarkChange(e.target.checked)}
            variant="rounded"
            label={t('modals.theme.form.darkTheme')}
          />
          {trailingContent}
        </div>
      </div>
    </div>
  );
};

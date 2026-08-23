import React from 'react';
import { Moon, Sun, Save } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Modal } from '../../ui/Modal';
import { Button } from '../../ui/Button';
import ThemeEditorForm from '../../features/management/theme/ThemeEditorForm';
import { ThemeFields } from './ThemeFields';
import { useColorHistory } from '@hooks/useColorHistory';
import { type EditableTheme } from '../../features/management/theme/types';
import themeService from '@services/theme.service';

interface CreateThemeModalProps {
  opened: boolean;
  onClose: () => void;
  onSave: () => void;
  isAdmin: boolean;
  newTheme: EditableTheme;
  setNewTheme: React.Dispatch<React.SetStateAction<EditableTheme>>;
  loading: boolean;
}

const CreateThemeModal: React.FC<CreateThemeModalProps> = ({
  opened,
  onClose,
  onSave,
  isAdmin,
  newTheme,
  setNewTheme,
  loading
}) => {
  const { t } = useTranslation();
  const colorHistory = useColorHistory('color_history_create');

  const loadPresetColors = (preset: 'dark' | 'light') => {
    const themeId = preset === 'dark' ? 'dark-default' : 'light-default';
    const builtInTheme = themeService
      .getBuiltInThemes()
      .find((theme: { meta: { id: string } }) => theme.meta.id === themeId);
    if (!builtInTheme) return;

    setNewTheme((prev) => ({
      ...prev,
      isDark: preset === 'dark',
      ...builtInTheme.colors
    }));
  };

  return (
    <Modal opened={opened} onClose={onClose} title={t('modals.theme.create.title')} size="xl">
      <div className="space-y-6">
        {/* Theme Metadata */}
        <ThemeFields
          name={newTheme.name}
          author={newTheme.author}
          description={newTheme.description}
          isDark={newTheme.isDark}
          onNameChange={(value) => setNewTheme({ ...newTheme, name: value })}
          onAuthorChange={(value) => setNewTheme({ ...newTheme, author: value })}
          onDescriptionChange={(value) => setNewTheme({ ...newTheme, description: value })}
          onDarkChange={(checked) => loadPresetColors(checked ? 'dark' : 'light')}
          trailingContent={
            <>
              <Button
                type="button"
                variant="default"
                size="xs"
                onClick={() => loadPresetColors('dark')}
                className="px-3 py-1 text-xs rounded-lg bg-themed-tertiary text-themed-secondary"
                leftSection={<Moon className="w-3 h-3" />}
              >
                {t('modals.theme.form.loadDarkPreset')}
              </Button>
              <Button
                type="button"
                variant="default"
                size="xs"
                onClick={() => loadPresetColors('light')}
                className="px-3 py-1 text-xs rounded-lg bg-themed-tertiary text-themed-secondary"
                leftSection={<Sun className="w-3 h-3" />}
              >
                {t('modals.theme.form.loadLightPreset')}
              </Button>
            </>
          }
        />

        {/* Color Editor */}
        <ThemeEditorForm
          themeData={newTheme}
          onColorChange={(key, value) => setNewTheme((prev) => ({ ...prev, [key]: value }))}
          onMetaChange={(key, value) => setNewTheme((prev) => ({ ...prev, [key]: value }))}
          colorHistory={colorHistory}
        />

        {/* Actions */}
        <div className="flex justify-end space-x-3 pt-4 border-t border-themed-primary">
          <Button variant="default" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button
            variant="filled"
            color="primary"
            leftSection={<Save className="w-4 h-4" />}
            onClick={onSave}
            disabled={!newTheme.name || !isAdmin || loading}
            className="themed-button-primary"
          >
            {t('modals.theme.create.saveButton')}
          </Button>
        </div>
      </div>
    </Modal>
  );
};

export default CreateThemeModal;

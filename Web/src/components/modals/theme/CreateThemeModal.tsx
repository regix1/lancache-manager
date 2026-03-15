import React from 'react';
import { Moon, Sun, Info, Save } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Modal } from '../../ui/Modal';
import { Button } from '../../ui/Button';
import { Checkbox } from '../../ui/Checkbox';
import ThemeEditorForm from '../../features/management/theme/ThemeEditorForm';
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
        <div className="space-y-4">
          <h4 className="text-sm font-semibold flex items-center gap-2 text-themed-primary">
            <Info className="w-4 h-4" />
            {t('modals.theme.form.themeInfo')}
          </h4>
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium mb-1 text-themed-secondary">
                {t('modals.theme.form.themeName')}
              </label>
              <input
                type="text"
                value={newTheme.name}
                onChange={(e) => setNewTheme({ ...newTheme, name: e.target.value })}
                placeholder={t('modals.theme.placeholders.themeName')}
                className="w-full px-3 py-2 focus:outline-none themed-input"
              />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1 text-themed-secondary">
                {t('modals.theme.form.author')}
              </label>
              <input
                type="text"
                value={newTheme.author}
                onChange={(e) => setNewTheme({ ...newTheme, author: e.target.value })}
                placeholder={t('modals.theme.placeholders.author')}
                className="w-full px-3 py-2 focus:outline-none themed-input"
              />
            </div>
          </div>
          <div>
            <label className="block text-sm font-medium mb-1 text-themed-secondary">
              {t('modals.theme.form.description')}
            </label>
            <input
              type="text"
              value={newTheme.description}
              onChange={(e) => setNewTheme({ ...newTheme, description: e.target.value })}
              placeholder={t('modals.theme.placeholders.description')}
              className="w-full px-3 py-2 rounded focus:outline-none themed-input"
            />
          </div>
          <div className="space-y-3">
            <div className="flex items-center gap-4">
              <Checkbox
                checked={newTheme.isDark}
                onChange={(e) => loadPresetColors(e.target.checked ? 'dark' : 'light')}
                variant="rounded"
                label={t('modals.theme.form.darkTheme')}
              />
              <button
                onClick={() => loadPresetColors('dark')}
                className="px-3 py-1 text-xs rounded-lg flex items-center gap-1 bg-themed-tertiary text-themed-secondary"
              >
                <Moon className="w-3 h-3" />
                {t('modals.theme.form.loadDarkPreset')}
              </button>
              <button
                onClick={() => loadPresetColors('light')}
                className="px-3 py-1 text-xs rounded-lg flex items-center gap-1 bg-themed-tertiary text-themed-secondary"
              >
                <Sun className="w-3 h-3" />
                {t('modals.theme.form.loadLightPreset')}
              </button>
            </div>
          </div>
        </div>

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

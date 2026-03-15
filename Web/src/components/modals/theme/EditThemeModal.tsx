import React from 'react';
import { Info, Save } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Modal } from '../../ui/Modal';
import { Button } from '../../ui/Button';
import { Checkbox } from '../../ui/Checkbox';
import ThemeEditorForm from '../../features/management/theme/ThemeEditorForm';
import { useColorHistory } from '@hooks/useColorHistory';
import { type Theme, type EditableTheme } from '../../features/management/theme/types';

interface EditThemeModalProps {
  opened: boolean;
  onClose: () => void;
  onSave: () => void;
  isAdmin: boolean;
  editingTheme: Theme | null;
  editedTheme: EditableTheme;
  setEditedTheme: React.Dispatch<React.SetStateAction<EditableTheme>>;
  loading: boolean;
}

const EditThemeModal: React.FC<EditThemeModalProps> = ({
  opened,
  onClose,
  onSave,
  isAdmin,
  editingTheme,
  editedTheme,
  setEditedTheme,
  loading
}) => {
  const { t } = useTranslation();
  const colorHistory = useColorHistory(`color_history_${editingTheme?.meta.id}`);

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={t('modals.theme.edit.title', { name: editingTheme?.meta.name || '' })}
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
                  {t('modals.theme.edit.communityNotice.title')}
                </p>
                <p className="text-xs text-themed-muted">
                  {t('modals.theme.edit.communityNotice.description', { name: editedTheme.name })}
                </p>
              </div>
            </div>
          </div>
        )}

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
                value={editedTheme.name || ''}
                onChange={(e) => setEditedTheme({ ...editedTheme, name: e.target.value })}
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
                value={editedTheme.author || ''}
                onChange={(e) => setEditedTheme({ ...editedTheme, author: e.target.value })}
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
              value={editedTheme.description || ''}
              onChange={(e) => setEditedTheme({ ...editedTheme, description: e.target.value })}
              placeholder={t('modals.theme.placeholders.description')}
              className="w-full px-3 py-2 rounded focus:outline-none themed-input"
            />
          </div>
          <div className="space-y-3">
            <div className="flex items-center gap-4">
              <Checkbox
                checked={editedTheme.isDark || false}
                onChange={(e) => setEditedTheme({ ...editedTheme, isDark: e.target.checked })}
                variant="rounded"
                label={t('modals.theme.form.darkTheme')}
              />
              <span className="text-xs text-themed-muted">
                {t('modals.theme.form.themeId', { id: editingTheme?.meta.id })}
              </span>
            </div>
          </div>
        </div>

        {/* Color Editor */}
        <ThemeEditorForm
          themeData={editedTheme}
          onColorChange={(key, value) => setEditedTheme((prev) => ({ ...prev, [key]: value }))}
          onMetaChange={(key, value) => setEditedTheme((prev) => ({ ...prev, [key]: value }))}
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
            disabled={!editedTheme.name || !isAdmin || loading}
            className="themed-button-primary"
          >
            {t('modals.theme.edit.saveButton')}
          </Button>
        </div>
      </div>
    </Modal>
  );
};

export default EditThemeModal;

import React, { useEffect, useRef, useState, type ChangeEvent } from 'react';
import { Save } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Modal } from '../../ui/Modal';
import { Button } from '../../ui/Button';
import { Alert } from '../../ui/Alert';
import { SegmentedControl } from '../../ui/SegmentedControl';
import FormField from '../../ui/FormField';
import { ConfirmationModal } from '../../common/ConfirmationModal';
import { CustomScrollbar } from '../../ui/CustomScrollbar';
import ThemeEditorForm from '../../features/management/theme/ThemeEditorForm';
import { ThemeFields } from './ThemeFields';
import { THEME_MODAL_CONTROL_SIZE, THEME_PREVIEW_SETTLE_MS, toPreviewTheme } from './constants';
import { useColorHistory } from '@hooks/useColorHistory';
import { useScrollAreaHeight } from '@hooks/useScrollAreaHeight';
import { type Theme, type EditableTheme } from '../../features/management/theme/types';
import themeService from '@services/theme.service';
import '@/styles/features/theme-editor-modal.css';

type ThemePane = 'basics' | 'colors' | 'customCss';

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
  const [pane, setPane] = useState<ThemePane>('basics');
  const [hasEdits, setHasEdits] = useState(false);
  const [confirmingClose, setConfirmingClose] = useState(false);
  const [setScrollArea, scrollAreaHeight] = useScrollAreaHeight();
  const openedDraftRef = useRef<string | null>(null);
  const previewAppliedRef = useRef(false);

  // Live preview. The draft is applied without persisting, so the whole app shows the edit while
  // the modal is open. It settles rather than repainting per frame: the picker fires onChange on
  // every pointer move and applyTheme rewrites the entire stylesheet.
  useEffect(() => {
    if (!opened) {
      openedDraftRef.current = null;
      setHasEdits(false);
      setPane('basics');
      return;
    }
    const draft = JSON.stringify(editedTheme);
    if (openedDraftRef.current === null) {
      openedDraftRef.current = draft;
      return;
    }
    // Recomputed rather than latched: undoing every change with the per-field restore puts the
    // draft back to what it was, and the preview has to follow it back down too.
    setHasEdits(draft !== openedDraftRef.current);
    // A draft opened before its theme finished loading carries no palette, and applying it then
    // would repaint the app from empty values.
    if (!editedTheme.primaryColor) return;
    const timer = setTimeout(() => {
      themeService.applyTheme(toPreviewTheme(editedTheme), { persist: false });
      previewAppliedRef.current = true;
    }, THEME_PREVIEW_SETTLE_MS);
    return () => clearTimeout(timer);
  }, [opened, editedTheme]);

  // Preview restyles the running app, so the theme that was applied when the modal opened is put
  // back on every way out: Cancel, the header X, the backdrop, Escape, and the close that follows
  // a successful save. Declared after the preview effect so its pending apply is canceled first.
  useEffect(() => {
    if (!opened) return;
    const restoreThemeId = themeService.getCurrentThemeId();
    return () => {
      if (!previewAppliedRef.current) return;
      previewAppliedRef.current = false;
      void themeService.setTheme(restoreThemeId);
    };
  }, [opened]);

  const requestClose = () => {
    if (hasEdits) {
      setConfirmingClose(true);
      return;
    }
    onClose();
  };

  return (
    <>
      <Modal
        opened={opened}
        onClose={requestClose}
        title={t('modals.theme.edit.title', { name: editingTheme?.meta.name || '' })}
        size="xl"
        bodyFlexLayout
      >
        <div className="theme-editor-modal">
          {editingTheme?.meta.isCommunityTheme && (
            <Alert color="blue" title={t('modals.theme.edit.communityNotice.title')}>
              {t('modals.theme.edit.communityNotice.description', { name: editedTheme.name })}
            </Alert>
          )}

          <SegmentedControl
            options={[
              { value: 'basics', label: t('modals.theme.form.themeInfo') },
              { value: 'colors', label: t('management.themes.customize.colorGroups') },
              { value: 'customCss', label: t('modals.theme.form.customCss') }
            ]}
            value={pane}
            onChange={(value) => setPane(value as ThemePane)}
            size={THEME_MODAL_CONTROL_SIZE}
            fullWidth
          />

          <div ref={setScrollArea} className="theme-editor-modal__scroll-area">
            <CustomScrollbar
              maxHeight={scrollAreaHeight != null ? `${scrollAreaHeight}px` : '100%'}
              radius="none"
            >
              <div className="theme-editor-modal__pane">
                {pane === 'basics' && (
                  <ThemeFields
                    name={editedTheme.name || ''}
                    author={editedTheme.author || ''}
                    description={editedTheme.description || ''}
                    isDark={editedTheme.isDark || false}
                    onNameChange={(value) => setEditedTheme({ ...editedTheme, name: value })}
                    onAuthorChange={(value) => setEditedTheme({ ...editedTheme, author: value })}
                    onDescriptionChange={(value) =>
                      setEditedTheme({ ...editedTheme, description: value })
                    }
                    onDarkChange={(checked) => setEditedTheme({ ...editedTheme, isDark: checked })}
                    themeData={editedTheme}
                    onColorChange={(key, value) =>
                      setEditedTheme((prev) => ({ ...prev, [key]: value }))
                    }
                    colorHistory={colorHistory}
                    trailingContent={
                      <span className="text-xs text-themed-muted">
                        {t('modals.theme.form.themeId', { id: editingTheme?.meta.id })}
                      </span>
                    }
                  />
                )}

                {pane === 'colors' && (
                  <ThemeEditorForm
                    themeData={editedTheme}
                    onColorChange={(key, value) =>
                      setEditedTheme((prev) => ({ ...prev, [key]: value }))
                    }
                    colorHistory={colorHistory}
                  />
                )}

                {pane === 'customCss' && (
                  <FormField label={t('modals.theme.form.customCss')}>
                    {(field) => (
                      <textarea
                        {...field}
                        value={editedTheme.customCSS || ''}
                        onChange={(e: ChangeEvent<HTMLTextAreaElement>) =>
                          setEditedTheme((prev) => ({ ...prev, customCSS: e.target.value }))
                        }
                        placeholder={t('modals.theme.placeholders.customCss')}
                        rows={12}
                        className="w-full px-3 py-2 font-mono text-xs focus:outline-none themed-input"
                      />
                    )}
                  </FormField>
                )}
              </div>
            </CustomScrollbar>
          </div>

          <div className="theme-editor-modal__actions">
            <Button variant="default" size={THEME_MODAL_CONTROL_SIZE} onClick={requestClose}>
              {t('common.cancel')}
            </Button>
            <Button
              variant="filled"
              color="primary"
              size={THEME_MODAL_CONTROL_SIZE}
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

      <ConfirmationModal
        opened={confirmingClose}
        onClose={() => setConfirmingClose(false)}
        onConfirm={() => {
          setConfirmingClose(false);
          onClose();
        }}
        title={t('management.sections.clients.unsavedChanges')}
      >
        <p className="text-sm text-themed-secondary">{t('prefill.confirm.defaultMessage')}</p>
      </ConfirmationModal>
    </>
  );
};

export default EditThemeModal;

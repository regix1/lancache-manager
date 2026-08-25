import React, { useEffect, useRef, useState, type ChangeEvent } from 'react';
import { Save } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Modal } from '../../ui/Modal';
import { Button } from '../../ui/Button';
import { SegmentedControl } from '../../ui/SegmentedControl';
import FormField from '../../ui/FormField';
import { ConfirmationModal } from '../../common/ConfirmationModal';
import { CustomScrollbar } from '../../ui/CustomScrollbar';
import ThemeEditorForm from '../../features/management/theme/ThemeEditorForm';
import { ThemeFields } from './ThemeFields';
import { THEME_MODAL_CONTROL_SIZE, THEME_PREVIEW_SETTLE_MS, toPreviewTheme } from './constants';
import { useColorHistory } from '@hooks/useColorHistory';
import { useScrollAreaHeight } from '@hooks/useScrollAreaHeight';
import { type EditableTheme } from '../../features/management/theme/types';
import themeService from '@services/theme.service';
import '@/styles/features/theme-editor-modal.css';

type ThemePane = 'basics' | 'colors' | 'customCss';

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
  const [pane, setPane] = useState<ThemePane>('basics');
  const [hasEdits, setHasEdits] = useState(false);
  const [confirmingClose, setConfirmingClose] = useState(false);
  const [setScrollArea, scrollAreaHeight] = useScrollAreaHeight();
  const openedDraftRef = useRef<string | null>(null);
  const previewAppliedRef = useRef(false);

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
    const draft = JSON.stringify(newTheme);
    if (openedDraftRef.current === null) {
      openedDraftRef.current = draft;
      return;
    }
    // Recomputed rather than latched: undoing every change with the per-field restore puts the
    // draft back to what it was, and the preview has to follow it back down too.
    setHasEdits(draft !== openedDraftRef.current);
    // The create form carries no palette until a preset is loaded, and applying it then would
    // repaint the app from empty values.
    if (!newTheme.primaryColor) return;
    const timer = setTimeout(() => {
      themeService.applyTheme(toPreviewTheme(newTheme), { persist: false });
      previewAppliedRef.current = true;
    }, THEME_PREVIEW_SETTLE_MS);
    return () => clearTimeout(timer);
  }, [opened, newTheme]);

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
        title={t('modals.theme.create.title')}
        size="xl"
        bodyFlexLayout
      >
        <div className="theme-editor-modal">
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
                    name={newTheme.name}
                    author={newTheme.author}
                    description={newTheme.description}
                    isDark={newTheme.isDark}
                    onNameChange={(value) => setNewTheme({ ...newTheme, name: value })}
                    onAuthorChange={(value) => setNewTheme({ ...newTheme, author: value })}
                    onDescriptionChange={(value) =>
                      setNewTheme({ ...newTheme, description: value })
                    }
                    onDarkChange={(checked) => loadPresetColors(checked ? 'dark' : 'light')}
                    themeData={newTheme}
                    onColorChange={(key, value) =>
                      setNewTheme((prev) => ({ ...prev, [key]: value }))
                    }
                    colorHistory={colorHistory}
                    trailingContent={
                      <>
                        <Button
                          type="button"
                          variant="default"
                          size={THEME_MODAL_CONTROL_SIZE}
                          onClick={() => loadPresetColors('dark')}
                        >
                          {t('modals.theme.form.loadDarkPreset')}
                        </Button>
                        <Button
                          type="button"
                          variant="default"
                          size={THEME_MODAL_CONTROL_SIZE}
                          onClick={() => loadPresetColors('light')}
                        >
                          {t('modals.theme.form.loadLightPreset')}
                        </Button>
                      </>
                    }
                  />
                )}

                {pane === 'colors' && (
                  <ThemeEditorForm
                    themeData={newTheme}
                    onColorChange={(key, value) =>
                      setNewTheme((prev) => ({ ...prev, [key]: value }))
                    }
                    colorHistory={colorHistory}
                  />
                )}

                {pane === 'customCss' && (
                  <FormField label={t('modals.theme.form.customCss')}>
                    {(field) => (
                      <textarea
                        {...field}
                        value={newTheme.customCSS || ''}
                        onChange={(e: ChangeEvent<HTMLTextAreaElement>) =>
                          setNewTheme((prev) => ({ ...prev, customCSS: e.target.value }))
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
              disabled={!newTheme.name || !isAdmin || loading}
              className="themed-button-primary"
            >
              {t('modals.theme.create.saveButton')}
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

export default CreateThemeModal;

import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { LogIn } from 'lucide-react';
import { AccessSetup } from '@components/initialization/AccessSetup';
import { LoginServiceMark } from '@components/features/auth/LoginServiceMark';
import { AccordionSection } from '@components/ui/AccordionSection';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { PasswordField } from '@components/ui/PasswordField';
import { SectionHeaderChip } from '@components/ui/SectionHeaderActions';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { useAuth } from '@contexts/useAuth';
import authService from '@services/auth.service';
import { usesOidc } from '@utils/accountMode';
import { getErrorMessage } from '@utils/error';
import type { LoginService } from '@utils/loginService';
import '@components/features/management/managementSectionContent.css';
import './user-settings.css';

/**
 * The installation-wide sign-in method and its tested connections, on the Accounts tab beside the
 * accounts it governs. The primary administrator changes the method here and removes connections;
 * an installation running without sign-in shows the row so the method can be switched on.
 */
const SignInMethodCard: React.FC = () => {
  const { t } = useTranslation();
  const { isMainAdmin, authenticationEnabled, accountMode, loginServices, refreshAuth } = useAuth();
  const [expanded, setExpanded] = useState(false);
  useAccordionGroupItem('accounts-sign-in-method', expanded, () => setExpanded((prev) => !prev));
  const [editingAccess, setEditingAccess] = useState(false);
  const [removing, setRemoving] = useState<LoginService | null>(null);
  const [removeKey, setRemoveKey] = useState('');
  const [removeBusy, setRemoveBusy] = useState(false);
  const [removeError, setRemoveError] = useState<string | null>(null);

  if (!isMainAdmin && authenticationEnabled) {
    return null;
  }

  const closeRemove = () => {
    if (removeBusy) return;
    setRemoving(null);
    setRemoveKey('');
    setRemoveError(null);
  };

  const removeService = async () => {
    if (!removing || !removeKey.trim()) return;
    setRemoveBusy(true);
    setRemoveError(null);
    try {
      await authService.removeLoginService(removing.id, removeKey.trim());
      setRemoving(null);
      setRemoveKey('');
      await refreshAuth();
    } catch (error: unknown) {
      setRemoveError(getErrorMessage(error) || t('accessSetup.removeFailed'));
    } finally {
      setRemoveBusy(false);
    }
  };

  const helpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('user.guest.sections.help.accessSecurityTitle')}>
        {t('accessSetup.manageDescription')}
      </HelpSection>
    </HelpPopover>
  );

  return (
    <>
      {editingAccess && <AccessSetup onClose={() => setEditingAccess(false)} />}
      <ConfirmationModal
        opened={removing !== null}
        onClose={closeRemove}
        onConfirm={() => void removeService()}
        loading={removeBusy}
        confirmDisabled={!removeKey.trim()}
        confirmLabel={t('accessSetup.remove')}
        title={t('accessSetup.removeService', { name: removing?.displayName ?? '' })}
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t(
              usesOidc(accountMode)
                ? 'accessSetup.removeServiceActive'
                : 'accessSetup.removeServiceDescription'
            )}
          </p>
          <PasswordField
            label={t('modals.auth.labels.apiKey')}
            value={removeKey}
            onChange={(event) => setRemoveKey(event.target.value)}
            disabled={removeBusy}
            autoComplete="new-password"
            inputClassName="w-full px-3 py-2.5 themed-input"
            showPasswordLabel={t('accessSetup.showSecret')}
            hidePasswordLabel={t('accessSetup.hideSecret')}
          />
          {removeError && (
            <div role="alert">
              <Alert color="error">{removeError}</Alert>
            </div>
          )}
        </div>
      </ConfirmationModal>
      <AccordionSection
        title={t('accessSetup.header')}
        titleAccessory={helpAccessory}
        icon={LogIn}
        isExpanded={expanded}
        onToggle={() => setExpanded((prev) => !prev)}
        badge={
          <SectionHeaderChip variant="neutral">
            {t(`accessSetup.modes.${accountMode}.title`)}
          </SectionHeaderChip>
        }
      >
        <div className="mgmt-list divided-list user-settings-list">
          <div className="mgmt-row">
            <div className="mgmt-row__body">
              <p className="mgmt-row__title">{t('accessSetup.signInMethod')}</p>
              <p className="mgmt-row__meta">{t(`accessSetup.modes.${accountMode}.title`)}</p>
            </div>
            <div className="mgmt-row__actions">
              <Button variant="default" onClick={() => setEditingAccess(true)}>
                {t('accessSetup.change')}
              </Button>
            </div>
          </div>
          {isMainAdmin &&
            loginServices.map((service) => {
              const kindTitle = t(`accessSetup.services.${service.kind}.title`);
              const use = t(
                usesOidc(accountMode)
                  ? 'accessSetup.connectionActive'
                  : 'accessSetup.connectionDormant'
              );
              return (
                <div key={service.id} className="mgmt-row">
                  <div className="mgmt-row__body">
                    <p className="mgmt-row__title flex items-center gap-2">
                      <LoginServiceMark kind={service.kind} />
                      <span>{service.displayName}</span>
                    </p>
                    <p className="mgmt-row__meta">
                      {kindTitle === service.displayName ? use : `${kindTitle} · ${use}`}
                    </p>
                  </div>
                  <div className="mgmt-row__actions">
                    <Button
                      variant="default"
                      disabled={removeBusy}
                      onClick={() => setRemoving(service)}
                    >
                      {t('accessSetup.remove')}
                    </Button>
                  </div>
                </div>
              );
            })}
        </div>
      </AccordionSection>
    </>
  );
};

export default SignInMethodCard;

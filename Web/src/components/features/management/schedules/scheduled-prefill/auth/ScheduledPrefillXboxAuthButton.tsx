import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { XboxAuthModal } from '@components/modals/auth/XboxAuthModal';
import { useScheduledPrefillXboxAuth } from '@hooks/useScheduledPrefillXboxAuth';

interface ScheduledPrefillXboxAuthButtonProps {
  disabled?: boolean;
  onSuccess?: (message: string) => void;
  onError?: (message: string) => void;
}

export function ScheduledPrefillXboxAuthButton({
  disabled = false,
  onSuccess,
  onError
}: ScheduledPrefillXboxAuthButtonProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config.auth';
  const [authModalOpened, setAuthModalOpened] = useState(false);
  const startInFlightRef = useRef(false);

  const { state, actions, startLogin, cancelLogin } = useScheduledPrefillXboxAuth({
    onSuccess: () => {
      setAuthModalOpened(false);
      onSuccess?.(t(`${baseKey}.actions.xboxLoginSuccess`));
    },
    onError
  });

  const handleLoginClick = () => {
    if (startInFlightRef.current) {
      return;
    }

    startInFlightRef.current = true;
    actions.resetAuthForm();
    setAuthModalOpened(true);
    void startLogin().finally(() => {
      startInFlightRef.current = false;
    });
  };

  const handleCloseAuthModal = () => {
    if (!state.loading) {
      setAuthModalOpened(false);
      actions.resetAuthForm();
    }
  };

  return (
    <>
      <Button
        type="button"
        variant="filled"
        size="sm"
        loading={state.loading}
        disabled={disabled}
        onClick={handleLoginClick}
      >
        {t(`${baseKey}.actions.logIn`)}
      </Button>
      <XboxAuthModal
        opened={authModalOpened}
        onClose={handleCloseAuthModal}
        state={state}
        actions={actions}
        onCancelLogin={cancelLogin}
      />
    </>
  );
}

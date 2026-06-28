import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { EpicAuthModal } from '@components/modals/auth/EpicAuthModal';
import { useEpicMappingAuth } from '@hooks/useEpicMappingAuth';

interface ScheduledPrefillEpicAuthButtonProps {
  disabled?: boolean;
  onSuccess?: (message: string) => void;
  onError?: (message: string) => void;
}

export function ScheduledPrefillEpicAuthButton({
  disabled = false,
  onSuccess,
  onError
}: ScheduledPrefillEpicAuthButtonProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config.auth';
  const [authModalOpened, setAuthModalOpened] = useState(false);
  const startInFlightRef = useRef(false);

  const { state, actions, startLogin } = useEpicMappingAuth({
    onSuccess: () => {
      setAuthModalOpened(false);
      onSuccess?.(t(`${baseKey}.actions.epicLoginSuccess`));
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
      <EpicAuthModal
        opened={authModalOpened}
        onClose={handleCloseAuthModal}
        state={state}
        actions={actions}
      />
    </>
  );
}

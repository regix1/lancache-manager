import React from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { LoginServiceMark } from './LoginServiceMark';
import type { LoginService } from '@utils/loginService';

interface LoginServiceButtonsProps {
  services: readonly LoginService[];
  /** The service whose challenge is being fetched; that button shows the spinner. */
  starting: string | null;
  disabled: boolean;
  onStart: (service: LoginService) => void;
}

/**
 * One "Continue with …" button per active connection. The three sign-in surfaces (gate, guest
 * upgrade tab, Steam management modal) draw the same list so a service added later shows up on
 * all of them at once. Google, GitHub, Microsoft and Apple wear the button treatment each of them
 * publishes; a custom OpenID Connect service wears the app's own primary button.
 */
export const LoginServiceButtons: React.FC<LoginServiceButtonsProps> = ({
  services,
  starting,
  disabled,
  onStart
}) => {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-3">
      {services.map((service) => {
        const branded = service.kind !== 'customOidc';
        return (
          <Button
            key={service.id}
            type="button"
            variant={branded ? 'transparent' : 'filled'}
            color="primary"
            fullWidth
            stableWidth
            className={branded ? `login-service-button login-service-button--${service.kind}` : ''}
            leftSection={branded ? <LoginServiceMark kind={service.kind} /> : undefined}
            loading={starting === service.id}
            disabled={disabled}
            onClick={() => onStart(service)}
          >
            {t('accessSetup.signInSso', { name: service.displayName })}
          </Button>
        );
      })}
    </div>
  );
};

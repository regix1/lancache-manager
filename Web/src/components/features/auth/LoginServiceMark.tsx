import React from 'react';
import { Globe } from 'lucide-react';
import { AppleIcon } from '@components/ui/AppleIcon';
import { GitHubIcon } from '@components/ui/GitHubIcon';
import { GoogleIcon } from '@components/ui/GoogleIcon';
import { MicrosoftIcon } from '@components/ui/MicrosoftIcon';
import type { LoginKind } from '@utils/loginService';
import '@/styles/features/login-service.css';

interface LoginServiceMarkProps {
  kind: LoginKind;
  className?: string;
}

/** The mark that identifies one sign-in service wherever it is offered or listed. */
export const LoginServiceMark: React.FC<LoginServiceMarkProps> = ({
  kind,
  className = 'login-service-mark'
}) => {
  switch (kind) {
    case 'google':
      return <GoogleIcon className={className} />;
    case 'github':
      return <GitHubIcon className={className} />;
    case 'microsoft':
      return <MicrosoftIcon className={className} />;
    case 'apple':
      return <AppleIcon className={className} />;
    default:
      return <Globe className={className} aria-hidden="true" />;
  }
};

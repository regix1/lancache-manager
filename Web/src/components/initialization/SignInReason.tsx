import React from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';

interface SignInReasonProps {
  reason: string;
  /** Reads the sign-in status again; set while the reason keeps the step's sign-in controls disabled. */
  onRetry?: () => void;
}

// Why a setup step cannot sign in. A status read that failed while the connection stayed up brings no event that
// would read it again, so the person can.
export const SignInReason: React.FC<SignInReasonProps> = ({ reason, onRetry }) => {
  const { t } = useTranslation();
  return (
    <div className="flex items-center justify-between gap-3">
      <p className="text-sm text-themed-muted" role="status">
        {reason}
      </p>
      {onRetry && (
        <Button variant="default" size="sm" onClick={onRetry}>
          {t('common.retry')}
        </Button>
      )}
    </div>
  );
};

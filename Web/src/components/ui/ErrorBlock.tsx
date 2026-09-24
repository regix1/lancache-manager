import React from 'react';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { useConnectionLost } from '@hooks/useConnectionLost';

interface ErrorBlockProps {
  title: string;
  message: string;
  retryLabel: string;
  onRetry: () => void;
  /** The caller's layout class, set on the box itself so its spacing hides with it. */
  className?: string;
}

// In-view fetch-failed block: an alert with Retry inside the box, for a view that failed to load.
export const ErrorBlock: React.FC<ErrorBlockProps> = ({
  title,
  message,
  retryLabel,
  onRetry,
  className
}) => {
  const connectionLost = useConnectionLost();

  // While the connection banner is up every request fails for the same reason, so the banner
  // speaks for this section.
  if (connectionLost) {
    return null;
  }

  return (
    <Alert
      color="error"
      title={title}
      className={className ? `w-full ${className}` : 'w-full'}
      action={
        <Button
          variant="filled"
          color="secondary"
          size="sm"
          className="pointer-target-44"
          onClick={onRetry}
        >
          {retryLabel}
        </Button>
      }
    >
      <p className="text-sm">{message}</p>
    </Alert>
  );
};

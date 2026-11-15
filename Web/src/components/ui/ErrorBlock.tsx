import React, { useRef, useState } from 'react';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { useConnectionLost } from '@hooks/useConnectionLost';

interface ErrorBlockProps {
  title: string;
  message: string;
  retryLabel: string;
  /** Retry shows busy until a returned promise settles. */
  onRetry: () => void | Promise<unknown>;
  /** True while a read Retry cannot hand back is still out; Retry shows it as busy. */
  retrying?: boolean;
  /** The caller's layout class, set on the box itself so its spacing hides with it. */
  className?: string;
}

// In-view fetch-failed block: an alert with Retry inside the box, for a view that failed to load.
export const ErrorBlock: React.FC<ErrorBlockProps> = ({
  title,
  message,
  retryLabel,
  onRetry,
  retrying = false,
  className
}) => {
  const connectionLost = useConnectionLost();
  const runningRef = useRef(false);
  const [running, setRunning] = useState(false);

  const retry = () => {
    if (runningRef.current) return;
    const read = onRetry();
    if (read instanceof Promise) {
      runningRef.current = true;
      setRunning(true);
      void read.finally(() => {
        runningRef.current = false;
        setRunning(false);
      });
    }
  };

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
          loading={retrying || running}
          onClick={retry}
        >
          {retryLabel}
        </Button>
      }
    >
      <p className="text-sm">{message}</p>
    </Alert>
  );
};

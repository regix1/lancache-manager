import React from 'react';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';

interface ErrorBlockProps {
  title: string;
  message: string;
  retryLabel: string;
  onRetry: () => void;
}

// In-view fetch-failed block: an alert plus a retry button, for a view that failed to load.
export const ErrorBlock: React.FC<ErrorBlockProps> = ({ title, message, retryLabel, onRetry }) => (
  <div className="prefill-error-state">
    <Alert color="error" title={title}>
      <p className="text-sm">{message}</p>
    </Alert>
    <div className="prefill-error-retry">
      <Button variant="filled" color="secondary" size="md" onClick={onRetry}>
        {retryLabel}
      </Button>
    </div>
  </div>
);

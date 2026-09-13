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
  <div className="flex w-full flex-col gap-3">
    <Alert color="error" title={title}>
      <p className="text-sm">{message}</p>
    </Alert>
    <div className="flex justify-start">
      <Button variant="filled" color="secondary" size="md" onClick={onRetry}>
        {retryLabel}
      </Button>
    </div>
  </div>
);

import React from 'react';
import { Alert } from '@components/ui/Alert';

interface AlertItem {
  id: number;
  message: string;
}

interface AlertsManagerProps {
  alerts: {
    errors?: AlertItem[];
    success?: string | null;
  };
  onClearError: (id: number) => void;
  onClearSuccess?: () => void;
}

const AlertsManager: React.FC<AlertsManagerProps> = ({ alerts, onClearError, onClearSuccess }) => {
  if (!alerts || (!alerts.errors?.length && !alerts.success)) {
    return null;
  }

  return (
    <div className="space-y-4">
      {alerts.errors?.map((error) => (
        <Alert key={error.id} color="red" withCloseButton onClose={() => onClearError(error.id)}>
          {error.message}
        </Alert>
      ))}

      {alerts.success && (
        <Alert color="green" withCloseButton={!!onClearSuccess} onClose={onClearSuccess}>
          {alerts.success}
        </Alert>
      )}
    </div>
  );
};

export default AlertsManager;

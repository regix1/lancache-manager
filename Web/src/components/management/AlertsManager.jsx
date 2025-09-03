import React from 'react';
import { AlertCircle, CheckCircle, X } from 'lucide-react';

const AlertsManager = ({ alerts, onClearError, onClearSuccess }) => {
    if (!alerts || (!alerts.errors?.length && !alerts.success)) {
        return null;
    }

    return (
        <>
            {/* Error Alerts */}
            {alerts.errors?.map(error => (
                <div key={error.id} className="bg-red-900 bg-opacity-30 rounded-lg p-4 border border-red-700">
                    <div className="flex items-start justify-between">
                        <div className="flex items-start space-x-2 flex-1">
                            <AlertCircle className="w-5 h-5 text-red-400 mt-0.5 flex-shrink-0" />
                            <span className="text-red-400">{error.message}</span>
                        </div>
                        <button
                            onClick={() => onClearError(error.id)}
                            className="ml-4 text-red-400 hover:text-red-300"
                        >
                            <X className="w-4 h-4" />
                        </button>
                    </div>
                </div>
            ))}

            {/* Success Alert */}
            {alerts.success && (
                <div className="bg-green-900 bg-opacity-30 rounded-lg p-4 border border-green-700">
                    <div className="flex items-center justify-between">
                        <div className="flex items-center space-x-2 flex-1">
                            <CheckCircle className="w-5 h-5 text-green-400" />
                            <span className="text-green-400">{alerts.success}</span>
                        </div>
                        {onClearSuccess && (
                            <button
                                onClick={onClearSuccess}
                                className="ml-4 text-green-400 hover:text-green-300"
                            >
                                <X className="w-4 h-4" />
                            </button>
                        )}
                    </div>
                </div>
            )}
        </>
    );
};

export default AlertsManager;
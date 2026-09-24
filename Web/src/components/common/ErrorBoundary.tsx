import React, { Component, type ReactNode } from 'react';
import i18n from '../../i18n';
import { getErrorMessage } from '@utils/error';
import { APP_EVENTS } from '@utils/constants';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';

interface Props {
  children: ReactNode;
}

interface State {
  hasError: boolean;
  error?: Error;
}

class ErrorBoundary extends Component<Props, State> {
  constructor(props: Props) {
    super(props);
    this.state = { hasError: false };
  }

  static getDerivedStateFromError(error: Error): State {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, errorInfo: React.ErrorInfo) {
    // Render-phase crash - the technical detail stays in the console/log; the reporting sink below
    // reaches the unified notification registry. This is a class component so the `useErrorHandler`
    // hook is unavailable - the `show-toast` CustomEvent bridge is the documented escape hatch for
    // non-hook code (NotificationsContext.tsx bridges it into the same generic notification).
    console.error('Error caught by boundary:', getErrorMessage(error), errorInfo);
    window.dispatchEvent(
      new CustomEvent(APP_EVENTS.SHOW_TOAST, {
        detail: { type: 'error', message: i18n.t('common.errorBoundary.title') }
      })
    );
  }

  render() {
    if (this.state.hasError) {
      return (
        <div className="error-boundary-root">
          <div className="error-boundary-container">
            {/* The title stays a real h2 for heading navigation; the base styles give a heading
                the inherited size and weight, so it looks like any other Alert title. */}
            <Alert
              color="error"
              title={<h2>{i18n.t('common.errorBoundary.title')}</h2>}
              className="w-full text-left"
              action={
                <Button
                  type="button"
                  variant="filled"
                  color="secondary"
                  size="sm"
                  className="pointer-target-44"
                  onClick={() => window.location.reload()}
                >
                  {i18n.t('common.errorBoundary.reload')}
                </Button>
              }
            >
              {/* Never render the raw error message to the user - the technical detail already
                  went to console/the reporting sink in componentDidCatch. */}
              <p className="text-sm">{i18n.t('common.errorBoundary.unexpectedError')}</p>
            </Alert>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}

export default ErrorBoundary;

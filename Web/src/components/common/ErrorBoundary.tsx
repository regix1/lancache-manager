import React, { Component, type ReactNode } from 'react';
import i18n from '../../i18n';

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
    console.error('Error caught by boundary:', error, errorInfo);
  }

  render() {
    if (this.state.hasError) {
      return (
        <div className="error-boundary-root">
          <div className="error-boundary-container">
            <div className="error-boundary-content">
              <div className="error-boundary-icon">
                <svg
                  width="32"
                  height="32"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                >
                  <circle cx="12" cy="12" r="10" />
                  <line x1="12" y1="8" x2="12" y2="12" />
                  <line x1="12" y1="16" x2="12.01" y2="16" />
                </svg>
              </div>

              <div className="error-boundary-body">
                <h2 className="error-boundary-title">{i18n.t('common.errorBoundary.title')}</h2>
                <pre className="error-boundary-message">
                  {this.state.error?.message || i18n.t('common.errorBoundary.unexpectedError')}
                </pre>
              </div>

              <button
                type="button"
                className="error-boundary-button"
                onClick={() => window.location.reload()}
              >
                {i18n.t('common.errorBoundary.reload')}
              </button>
            </div>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}

export default ErrorBoundary;

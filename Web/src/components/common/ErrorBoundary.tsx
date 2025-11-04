import React, { Component, type ReactNode } from 'react';
import { AlertCircle, RefreshCw } from 'lucide-react';
import { Card } from '../ui/Card';
import { Button } from '../ui/Button';

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
        <div className="min-h-screen bg-themed-primary flex items-center justify-center p-4">
          <Card className="max-w-md w-full">
            <div className="flex flex-col items-center text-center space-y-4">
              <div
                className="p-3 rounded-full"
                style={{ backgroundColor: 'var(--theme-error-bg)' }}
              >
                <AlertCircle className="w-8 h-8" style={{ color: 'var(--theme-error)' }} />
              </div>

              <div className="space-y-2 w-full">
                <h3 className="text-xl font-semibold text-themed-primary">Something went wrong</h3>
                <p className="text-sm text-themed-secondary break-words overflow-wrap-anywhere px-2">
                  {this.state.error?.message || 'An unexpected error occurred'}
                </p>
              </div>

              <Button
                onClick={() => window.location.reload()}
                variant="filled"
                color="red"
                leftSection={<RefreshCw className="w-4 h-4" />}
                fullWidth
              >
                Reload Page
              </Button>
            </div>
          </Card>
        </div>
      );
    }

    return this.props.children;
  }
}

export default ErrorBoundary;

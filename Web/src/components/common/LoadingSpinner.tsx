import React, { useEffect, useRef } from 'react';
import { Loader2 } from 'lucide-react';

interface LoadingSpinnerProps {
  message?: string;
  size?: 'xs' | 'sm' | 'md' | 'lg' | 'xl';
  fullScreen?: boolean;
}

const LoadingSpinner: React.FC<LoadingSpinnerProps> = ({
  message,
  size = 'md',
  fullScreen = false
}) => {
  const containerRef = useRef<HTMLDivElement>(null);

  // Force browser to keep rendering on mobile by periodically touching the DOM
  // This prevents mobile browsers from throttling updates when spinner is visible
  useEffect(() => {
    let frameId: number;
    let lastTime = 0;

    const keepAlive = (time: number) => {
      // Only update every 500ms to minimize overhead
      if (time - lastTime > 500) {
        lastTime = time;
        // Force a style recalc to keep the browser rendering
        if (containerRef.current) {
          void containerRef.current.offsetHeight;
        }
      }
      frameId = requestAnimationFrame(keepAlive);
    };

    frameId = requestAnimationFrame(keepAlive);
    return () => cancelAnimationFrame(frameId);
  }, []);

  const sizeClasses = {
    xs: 'w-4 h-4',
    sm: 'w-6 h-6',
    md: 'w-8 h-8',
    lg: 'w-12 h-12',
    xl: 'w-16 h-16'
  };

  const content = (
    <div ref={containerRef} className="flex flex-col items-center justify-center space-y-4">
      <Loader2
        className={`${sizeClasses[size]} text-themed-accent animate-spin`}
        style={{ willChange: 'transform' }}
      />
      {message && <p className="text-sm text-themed-secondary">{message}</p>}
    </div>
  );

  if (fullScreen) {
    return (
      <div className="fixed inset-0 bg-themed-primary bg-opacity-50 flex items-center justify-center z-50">
        {content}
      </div>
    );
  }

  return <div className="flex items-center justify-center min-h-[200px]">{content}</div>;
};

export default LoadingSpinner;

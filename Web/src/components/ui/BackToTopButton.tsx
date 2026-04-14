import { useEffect, useState, type RefObject } from 'react';
import { ChevronUp } from 'lucide-react';
import './BackToTopButton.css';

interface BackToTopButtonProps {
  scrollContainerRef: RefObject<HTMLElement | null>;
  threshold?: number;
  className?: string;
}

export function BackToTopButton({
  scrollContainerRef,
  threshold = 300,
  className
}: BackToTopButtonProps): React.ReactElement {
  const [visible, setVisible] = useState<boolean>(false);

  useEffect(() => {
    const el = scrollContainerRef.current;
    if (!el) return;
    const handler = (): void => setVisible(el.scrollTop > threshold);
    handler();
    el.addEventListener('scroll', handler, { passive: true });
    return () => {
      el.removeEventListener('scroll', handler);
    };
  }, [scrollContainerRef, threshold]);

  const handleClick = (): void => {
    scrollContainerRef.current?.scrollTo({ top: 0, behavior: 'smooth' });
  };

  const classes = ['back-to-top-button', visible ? 'is-visible' : '', className ?? '']
    .filter(Boolean)
    .join(' ');

  return (
    <button
      type="button"
      aria-label="Back to top"
      className={classes}
      onClick={handleClick}
      tabIndex={visible ? 0 : -1}
    >
      <ChevronUp size={18} aria-hidden="true" />
    </button>
  );
}

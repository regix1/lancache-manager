import { useEffect, useState, type ReactNode } from 'react';
import { AccordionSection } from '@components/ui/AccordionSection';

interface ScheduledPrefillPlatformSubsectionProps {
  title: string;
  defaultExpanded?: boolean;
  resetKey?: string;
  badge?: ReactNode;
  children: ReactNode;
}

export function ScheduledPrefillPlatformSubsection({
  title,
  defaultExpanded = false,
  resetKey,
  badge,
  children
}: ScheduledPrefillPlatformSubsectionProps) {
  const [isExpanded, setIsExpanded] = useState(defaultExpanded);

  useEffect(() => {
    setIsExpanded(defaultExpanded);
  }, [defaultExpanded, resetKey]);

  return (
    <div className="scheduled-prefill-platform-subsection">
      <AccordionSection
        title={title}
        isExpanded={isExpanded}
        onToggle={() => setIsExpanded((current) => !current)}
        badge={badge}
      >
        <div className="scheduled-prefill-platform-subsection__content">{children}</div>
      </AccordionSection>
    </div>
  );
}

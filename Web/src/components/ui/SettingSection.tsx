import React from 'react';

interface SettingSectionProps {
  icon: React.ElementType;
  title: string;
  children: React.ReactNode;
}

/**
 * A sub-section on a well surface: icon chip, title, divider, then the rows. The chip takes the
 * theme accent rather than a per-section colour, which is the rule the section headings above it
 * follow, so a page of these reads as one list instead of a row of unrelated colours.
 */
export const SettingSection: React.FC<SettingSectionProps> = ({ icon: Icon, title, children }) => (
  <div className="p-4 rounded-lg bg-themed-tertiary">
    <div className="flex items-center gap-2 mb-3 pb-2 border-b border-themed-secondary">
      <div className="icon-box icon-box--sm bg-themed-accent-subtle">
        <Icon className="w-4 h-4 text-themed-accent" />
      </div>
      <h4 className="mgmt-subhead caps-label">{title}</h4>
    </div>
    <div className="space-y-3">{children}</div>
  </div>
);

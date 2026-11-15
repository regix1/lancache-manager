import React from 'react';

interface UserPaneSectionProps {
  title: string;
  /** A HelpPopover shown after the title. */
  help?: React.ReactNode;
  children: React.ReactNode;
}

/** One titled group in the Users detail pane body. */
const UserPaneSection: React.FC<UserPaneSectionProps> = ({ title, help, children }) => (
  <section className="user-settings-group">
    <h3 className="users-section__title">
      {title}
      {help}
    </h3>
    {children}
  </section>
);

export default UserPaneSection;

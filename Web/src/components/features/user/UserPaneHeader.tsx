import React from 'react';

interface UserPaneHeaderProps {
  title: string;
  /** The first line reads as the summary, the rest as muted detail. Empty renders no line block. */
  lines: React.ReactNode[];
  actions?: React.ReactNode;
}

/** The band at the top of the Users detail pane: the picked item's name, its lines, its actions. */
const UserPaneHeader: React.FC<UserPaneHeaderProps> = ({ title, lines, actions }) => (
  <header className="users-pane-head">
    <div className="users-pane-head__text">
      <h2 className="users-pane-head__title">{title}</h2>
      {lines.map((line: React.ReactNode, index: number) => (
        <p
          // The lines are fixed positions in one header, never reordered, so the index is their identity.
          key={index}
          className={
            index === 0 ? 'users-pane-head__line' : 'users-pane-head__line text-themed-muted'
          }
        >
          {line}
        </p>
      ))}
    </div>
    {actions !== undefined && <div className="mgmt-row__actions">{actions}</div>}
  </header>
);

export default UserPaneHeader;

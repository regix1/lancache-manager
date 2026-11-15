import React from 'react';

interface UserSettingRowProps {
  label: React.ReactNode;
  /** A HelpPopover shown after the label. */
  help?: React.ReactNode;
  children: React.ReactNode;
}

/** One setting in the Users detail pane: label on the left, its controls on the right at one height. */
const UserSettingRow: React.FC<UserSettingRowProps> = ({ label, help, children }) => (
  <div className="user-settings-field-row">
    <div className="user-settings-field-label">
      <span>{label}</span>
      {help}
    </div>
    <div className="user-settings-field-controls">{children}</div>
  </div>
);

export default UserSettingRow;

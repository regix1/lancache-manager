import React from 'react';
import { ChevronRight } from 'lucide-react';

interface UserListRowProps {
  /** A StatusDot, or a muted 16 px icon for the pinned entries. */
  lead: React.ReactNode;
  title: string;
  /** Muted word after the title, such as "you". */
  aside?: string;
  /** One muted line under the title; null renders no line element. */
  line: React.ReactNode | null;
  selected: boolean;
  /** The pinned settings entries, which read quieter than the people below them. */
  quiet?: boolean;
  /** A disabled account, drawn grayed out and still pickable so it can be enabled again. */
  dimmed?: boolean;
  onSelect: () => void;
}

/** One pickable row of the Users list: the whole row is the button. */
const UserListRow: React.FC<UserListRowProps> = ({
  lead,
  title,
  aside,
  line,
  selected,
  quiet = false,
  dimmed = false,
  onSelect
}) => (
  <li>
    <button
      type="button"
      className={`users-row${quiet ? ' users-row--quiet' : ''}${dimmed ? ' users-row--dimmed' : ''}`}
      aria-current={selected ? 'true' : undefined}
      onClick={onSelect}
    >
      <span className="users-row__lead">{lead}</span>
      <span className="users-row__body">
        <span className="users-row__title-line">
          <span className={quiet ? 'users-row__title--quiet' : 'mgmt-row__title'}>{title}</span>
          {aside !== undefined && <span className="users-row__aside">{aside}</span>}
        </span>
        {line !== null && <span className="users-row__line">{line}</span>}
      </span>
      <ChevronRight className="users-row__chevron" aria-hidden="true" />
    </button>
  </li>
);

export default UserListRow;

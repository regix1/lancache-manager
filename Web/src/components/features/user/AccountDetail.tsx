import React, { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { RowActionsMenu } from '@components/ui/RowActionsMenu';
import { ActionMenuDivider, ActionMenuItem } from '@components/ui/ActionMenu';
import { useAuth } from '@contexts/useAuth';
import { useReaderClock } from '@hooks/useReaderClock';
import { formatTimestamp } from '@utils/dateTimeFormat';
import UserPaneHeader from './UserPaneHeader';
import { formatLastSeen } from './sessionTime';
import type { AccountActions, UserAccount } from './types';

interface AccountDetailProps {
  account: UserAccount;
  accountActions: AccountActions;
}

/** The detail pane of an account with no signed-in session: its facts and account actions. */
const AccountDetail: React.FC<AccountDetailProps> = ({ account, accountActions }) => {
  const { t } = useTranslation();
  const { accountId, sessionType } = useAuth();
  const [menuOpen, setMenuOpen] = useState(false);
  const clock = useReaderClock();
  const created = useMemo(
    () => formatTimestamp(account.createdAtUtc, { ...clock, forceYear: false, style: 'dateOnly' }),
    [clock, account.createdAtUtc]
  );
  // The server refuses edit, disable and delete for the primary admin, and disable and delete of
  // your own account, so those actions are left out rather than shown disabled.
  const primaryAdmin = account.isMainAdmin;
  const yourself = account.id === accountId;
  // A user-role account changes only its own account, so another account offers it no actions.
  const mayChange = sessionType !== 'user' || yourself;

  const summary = [
    primaryAdmin ? t('user.accounts.mainAdmin') : t('user.accounts.roles.user'),
    ...(account.isDisabled ? [t('user.panes.list.disabled')] : []),
    account.lastLoginAtUtc == null
      ? t('user.panes.list.neverSignedIn')
      : t('user.panes.list.lastSignedIn', { when: formatLastSeen(account.lastLoginAtUtc, t) }),
    t('user.panes.list.created', { when: created })
  ].join(t('user.panes.separator'));

  const lines: React.ReactNode[] = [summary];
  if (primaryAdmin) lines.push(t('user.panes.help.primaryAdmin'));

  return (
    <UserPaneHeader
      title={account.username}
      lines={lines}
      actions={
        primaryAdmin || !mayChange ? undefined : (
          <>
            {!yourself && (
              <Button
                variant="default"
                size="sm"
                className="users-pane-btn pointer-target-44"
                loading={accountActions.disablingAccountId === account.id}
                onClick={() => accountActions.onSetDisabled(account, !account.isDisabled)}
              >
                {account.isDisabled
                  ? t('user.accounts.actions.enable')
                  : t('user.accounts.actions.disable')}
              </Button>
            )}
            <RowActionsMenu
              open={menuOpen}
              onOpenChange={setMenuOpen}
              aria-label={t('user.panes.menu.label', { name: account.username })}
            >
              {(close: () => void) => (
                <>
                  <ActionMenuItem
                    onClick={() => {
                      close();
                      accountActions.onEdit(account);
                    }}
                  >
                    {t('user.panes.menu.editAccount')}
                  </ActionMenuItem>
                  {!yourself && (
                    <>
                      <ActionMenuDivider />
                      <ActionMenuItem
                        onClick={() => {
                          close();
                          accountActions.onDelete(account);
                        }}
                      >
                        {t('user.panes.menu.deleteAccount')}
                      </ActionMenuItem>
                    </>
                  )}
                </>
              )}
            </RowActionsMenu>
          </>
        )
      }
    />
  );
};

export default AccountDetail;

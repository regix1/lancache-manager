import React, { useCallback, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { UserCog, MoreVertical } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { Alert } from '@components/ui/Alert';
import { Tooltip } from '@components/ui/Tooltip';
import Badge from '@components/ui/Badge';
import { AccordionSection } from '@components/ui/AccordionSection';
import { SectionHeaderActions } from '@components/ui/SectionHeaderActions';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { DataTable, type DataTableColumn } from '@components/ui/DataTable';
import { Pagination } from '@components/ui/Pagination';
import {
  ActionMenu,
  ActionMenuItem,
  ActionMenuDivider,
  ActionMenuDangerItem
} from '@components/ui/ActionMenu';
import { EmptyState, LoadingState } from '@components/ui/ManagerCard';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { FormattedTimestamp } from '@components/common/FormattedDateTime';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { getErrorMessage } from '@utils/error';
import { API_BASE } from '@utils/constants';
import type { AccountConfirmation, AccountEditor, AccountRole, UserAccount } from './types';

// Not exported - a .tsx file exports components only, and Fast Refresh needs it that way.
const PAGE_SIZE = 10;

/**
 * Reads and writes the accounts people sign in with, as the third segment of the user tab.
 *
 * Nothing here decides who may do what. The account rows a caller is answered with, and every
 * refusal, come from the server, so a user and an admin run the same screen and see whatever their
 * own session is entitled to. The one thing the screen does decide is what a control that is going
 * to be refused looks like: the account that owns the installation keeps its row menu, with the
 * items on it disabled, because a control that vanishes reads as a bug while a disabled one reads
 * as a rule. [69]
 */
const UserAccounts: React.FC = () => {
  const { t } = useTranslation();
  const { notifyError } = useErrorHandler();
  const { isMainAdmin } = useAuth();

  const [accounts, setAccounts] = useState<UserAccount[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadFailed, setLoadFailed] = useState(false);
  const [expanded, setExpanded] = useState(true);
  const [currentPage, setCurrentPage] = useState(1);
  const [openMenuAccountId, setOpenMenuAccountId] = useState<string | null>(null);
  const [busyAccountId, setBusyAccountId] = useState<string | null>(null);
  const [confirmation, setConfirmation] = useState<AccountConfirmation | null>(null);
  const [editor, setEditor] = useState<AccountEditor | null>(null);
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);

  const loadAccounts = useCallback(async () => {
    try {
      setLoading(true);
      const response = await fetch(`${API_BASE}/accounts`, ApiService.getFetchOptions({}));
      setAccounts(await ApiService.handleResponse<UserAccount[]>(response));
      setLoadFailed(false);
    } catch (err: unknown) {
      // The list stays empty on a failure, so without this the screen would show the empty state
      // and report "no accounts" for what is actually a request that never arrived.
      setLoadFailed(true);
      notifyError(t('user.accounts.errors.load'), err, { logLabel: 'Failed to load accounts' });
    } finally {
      setLoading(false);
    }
  }, [notifyError, t]);

  useEffect(() => {
    loadAccounts();
  }, [loadAccounts]);

  const submitEditor = async (open: AccountEditor) => {
    try {
      setSaving(true);
      setFormError(null);

      const url = open.account ? `${API_BASE}/accounts/${open.account.id}` : `${API_BASE}/accounts`;
      const body = open.account
        ? // An edit that leaves the password blank is a rename: null tells the server to keep the
          // stored password rather than replace it with an empty one.
          { username: open.username, password: open.password ? open.password : null }
        : { username: open.username, password: open.password, role: open.role };

      const response = await fetch(
        url,
        ApiService.getJsonFetchOptions(body, { method: open.account ? 'PUT' : 'POST' })
      );
      const saved = await ApiService.handleResponse<UserAccount>(response);

      setAccounts((prev: UserAccount[]) =>
        (open.account
          ? prev.map((account: UserAccount) => (account.id === saved.id ? saved : account))
          : [...prev, saved]
        ).sort((a: UserAccount, b: UserAccount) => a.username.localeCompare(b.username))
      );
      setEditor(null);
    } catch (err: unknown) {
      // Shown in the form rather than as a notification: the refusal names the field the person is
      // looking at ("that username is already taken"), and the form stays open to be corrected.
      setFormError(getErrorMessage(err));
    } finally {
      setSaving(false);
    }
  };

  const setDisabled = async (account: UserAccount, disabled: boolean) => {
    try {
      setBusyAccountId(account.id);
      const response = await fetch(
        `${API_BASE}/accounts/${account.id}/disabled`,
        ApiService.getJsonFetchOptions({ disabled }, { method: 'PUT' })
      );
      const saved = await ApiService.handleResponse<UserAccount>(response);
      setAccounts((prev: UserAccount[]) =>
        prev.map((existing: UserAccount) => (existing.id === saved.id ? saved : existing))
      );
    } catch (err: unknown) {
      notifyError(t('user.accounts.errors.disabled'), err, {
        logLabel: 'Failed to change whether an account is disabled'
      });
    } finally {
      setBusyAccountId(null);
    }
  };

  const runConfirmation = async (pending: AccountConfirmation) => {
    const { account } = pending;
    try {
      setBusyAccountId(account.id);

      if (pending.kind === 'delete') {
        const response = await fetch(
          `${API_BASE}/accounts/${account.id}`,
          ApiService.getFetchOptions({ method: 'DELETE' })
        );
        await ApiService.handleResponse<{ message: string }>(response);
        setAccounts((prev: UserAccount[]) =>
          prev.filter((existing: UserAccount) => existing.id !== account.id)
        );
      } else {
        const role: AccountRole = account.role === 'admin' ? 'user' : 'admin';
        const response = await fetch(
          `${API_BASE}/accounts/${account.id}/role`,
          ApiService.getJsonFetchOptions({ role }, { method: 'PUT' })
        );
        const saved = await ApiService.handleResponse<UserAccount>(response);
        setAccounts((prev: UserAccount[]) =>
          prev.map((existing: UserAccount) => (existing.id === saved.id ? saved : existing))
        );
      }

      setConfirmation(null);
    } catch (err: unknown) {
      notifyError(
        pending.kind === 'delete'
          ? t('user.accounts.errors.delete')
          : t('user.accounts.errors.role'),
        err,
        { logLabel: 'Failed to change an account' }
      );
    } finally {
      setBusyAccountId(null);
    }
  };

  // Deleting the last row of the last page leaves currentPage past the end until the pager is
  // touched again, which renders as a blank table.
  const totalPages = Math.max(1, Math.ceil(accounts.length / PAGE_SIZE));
  const safePage = Math.min(currentPage, totalPages);
  const visibleAccounts = accounts.slice((safePage - 1) * PAGE_SIZE, safePage * PAGE_SIZE);

  const columns: DataTableColumn<UserAccount>[] = [
    {
      key: 'username',
      header: t('user.accounts.columns.username'),
      width: 'minmax(160px, 2fr)',
      render: (account: UserAccount) => (
        <span className="truncate text-themed-primary">{account.username}</span>
      )
    },
    {
      key: 'role',
      header: t('user.accounts.columns.role'),
      width: 'minmax(150px, 1fr)',
      render: (account: UserAccount) => (
        <div className="flex items-center gap-2">
          <Badge variant={account.role === 'admin' ? 'info' : 'neutral'}>
            {account.role === 'admin'
              ? t('user.accounts.roles.admin')
              : t('user.accounts.roles.user')}
          </Badge>
          {/* Names the rule behind the disabled row menu, so the greyed-out items read as
              deliberate rather than broken. [69] */}
          {account.isMainAdmin && <Badge variant="warning">{t('user.accounts.mainAdmin')}</Badge>}
        </div>
      )
    },
    {
      key: 'status',
      header: t('user.accounts.columns.status'),
      width: 'minmax(110px, 1fr)',
      render: (account: UserAccount) => (
        <Badge variant={account.isDisabled ? 'error' : 'success'}>
          {account.isDisabled
            ? t('user.accounts.status.disabled')
            : t('user.accounts.status.active')}
        </Badge>
      )
    },
    {
      key: 'created',
      header: t('user.accounts.columns.created'),
      width: 'minmax(140px, 1fr)',
      cellClassName: 'tabular-nums',
      render: (account: UserAccount) => <FormattedTimestamp timestamp={account.createdAtUtc} />
    },
    {
      key: 'lastLogin',
      header: t('user.accounts.columns.lastSignIn'),
      width: 'minmax(140px, 1fr)',
      cellClassName: 'tabular-nums',
      render: (account: UserAccount) =>
        account.lastLoginAtUtc ? (
          <FormattedTimestamp timestamp={account.lastLoginAtUtc} />
        ) : (
          <span className="text-themed-muted">{t('user.accounts.neverSignedIn')}</span>
        )
    },
    {
      key: 'actions',
      header: t('user.accounts.columns.actions'),
      width: '72px',
      align: 'center',
      render: (account: UserAccount) => {
        // The installation's own account refuses every one of these on the server, and a promotion
        // is refused for anyone but the main administrator. Both are shown disabled. [69]
        const owner = account.isMainAdmin;
        const busy = busyAccountId === account.id;
        const promoting = account.role !== 'admin';

        return (
          <ActionMenu
            isOpen={openMenuAccountId === account.id}
            onClose={() => setOpenMenuAccountId(null)}
            align="right"
            trigger={
              <Tooltip content={t('common.moreActions')}>
                <Button
                  variant="filled"
                  color="gray"
                  size="sm"
                  className="btn-icon-square btn-icon-square--sm pointer-target-44"
                  onClick={() =>
                    setOpenMenuAccountId((prev: string | null) =>
                      prev === account.id ? null : account.id
                    )
                  }
                  aria-label={t('common.moreActions')}
                >
                  <MoreVertical className="w-4 h-4" />
                </Button>
              </Tooltip>
            }
          >
            <ActionMenuItem
              disabled={owner || busy}
              onClick={() => {
                setOpenMenuAccountId(null);
                setFormError(null);
                setEditor({
                  account,
                  username: account.username,
                  password: '',
                  role: account.role
                });
              }}
            >
              {t('common.edit')}
            </ActionMenuItem>
            <ActionMenuItem
              disabled={owner || busy || (promoting && !isMainAdmin)}
              onClick={() => {
                setOpenMenuAccountId(null);
                setConfirmation({ kind: 'role', account });
              }}
            >
              {promoting ? t('user.accounts.actions.promote') : t('user.accounts.actions.demote')}
            </ActionMenuItem>
            <ActionMenuItem
              disabled={owner || busy}
              onClick={() => {
                setOpenMenuAccountId(null);
                setDisabled(account, !account.isDisabled);
              }}
            >
              {account.isDisabled
                ? t('user.accounts.actions.enable')
                : t('user.accounts.actions.disable')}
            </ActionMenuItem>
            <ActionMenuDivider />
            <ActionMenuDangerItem
              disabled={owner || busy}
              onClick={() => {
                setOpenMenuAccountId(null);
                setConfirmation({ kind: 'delete', account });
              }}
            >
              {t('common.delete')}
            </ActionMenuDangerItem>
          </ActionMenu>
        );
      }
    }
  ];

  return (
    <>
      <AccordionSection
        title={t('user.accounts.title')}
        icon={UserCog}
        iconColor="var(--theme-icon-blue)"
        count={!loading && accounts.length > 0 ? accounts.length : undefined}
        isExpanded={expanded}
        onToggle={() => setExpanded((prev: boolean) => !prev)}
        badge={
          <SectionHeaderActions>
            <Button
              variant="filled"
              color="blue"
              size="sm"
              onClick={() => {
                setFormError(null);
                setEditor({ account: null, username: '', password: '', role: 'user' });
              }}
            >
              {t('user.accounts.actions.create')}
            </Button>
          </SectionHeaderActions>
        }
      >
        <div className="space-y-4">
          {loading ? (
            <LoadingState message={t('user.accounts.loading')} />
          ) : loadFailed ? (
            <Alert color="red">{t('user.accounts.errors.load')}</Alert>
          ) : accounts.length === 0 ? (
            // No icon: the section header already carries UserCog, and repeating it here would put
            // the same icon twice on one item.
            <EmptyState
              title={t('user.accounts.empty.title')}
              subtitle={t('user.accounts.empty.subtitle')}
            />
          ) : (
            <>
              <DataTable<UserAccount>
                columns={columns}
                data={visibleAccounts}
                keyExtractor={(account: UserAccount) => account.id}
                striped
                compact
              />
              {accounts.length > PAGE_SIZE && (
                <Pagination
                  currentPage={safePage}
                  totalPages={totalPages}
                  totalItems={accounts.length}
                  itemsPerPage={PAGE_SIZE}
                  onPageChange={(page: number) => setCurrentPage(page)}
                  itemLabel={t('user.accounts.paginationLabel')}
                  showCard={false}
                />
              )}
            </>
          )}
        </div>
      </AccordionSection>

      {editor && (
        <Modal
          opened
          onClose={() => {
            if (!saving) {
              setEditor(null);
            }
          }}
          title={
            editor.account
              ? t('user.accounts.form.editTitle', { username: editor.account.username })
              : t('user.accounts.form.createTitle')
          }
        >
          <div className="space-y-4">
            <div>
              <label className="form-field-label" htmlFor="account-username">
                {t('user.accounts.form.username')}
              </label>
              <input
                id="account-username"
                type="text"
                autoComplete="username"
                className="w-full px-3 py-2.5 themed-input"
                value={editor.username}
                onChange={(event: React.ChangeEvent<HTMLInputElement>) =>
                  setEditor({ ...editor, username: event.target.value })
                }
              />
            </div>

            <div>
              <label className="form-field-label" htmlFor="account-password">
                {editor.account
                  ? t('user.accounts.form.newPassword')
                  : t('user.accounts.form.password')}
              </label>
              <input
                id="account-password"
                type="password"
                autoComplete="new-password"
                className="w-full px-3 py-2.5 themed-input"
                value={editor.password}
                onChange={(event: React.ChangeEvent<HTMLInputElement>) =>
                  setEditor({ ...editor, password: event.target.value })
                }
              />
              {editor.account && (
                <p className="text-xs text-themed-muted mt-1">
                  {t('user.accounts.form.passwordHint')}
                </p>
              )}
            </div>

            {!editor.account && (
              <div>
                {/* No htmlFor: the dropdown's control is a button, not a labelable field, so the
                    name reaches it through triggerAriaLabel instead. */}
                <label className="form-field-label">{t('user.accounts.form.role')}</label>
                <EnhancedDropdown
                  triggerAriaLabel={t('user.accounts.form.role')}
                  options={[
                    { value: 'user', label: t('user.accounts.roles.user') },
                    {
                      value: 'admin',
                      label: t('user.accounts.roles.admin'),
                      // Only the installation's own account hands out the administrator role, so
                      // for everyone else the choice is shown and refused rather than hidden.
                      disabled: !isMainAdmin,
                      tooltip: isMainAdmin ? undefined : t('user.accounts.form.adminRoleReserved')
                    }
                  ]}
                  value={editor.role}
                  onChange={(value: string) =>
                    setEditor({ ...editor, role: value === 'admin' ? 'admin' : 'user' })
                  }
                />
              </div>
            )}

            {formError && <p className="text-sm text-themed-error">{formError}</p>}

            <div className="flex justify-end gap-2">
              <Button variant="default" disabled={saving} onClick={() => setEditor(null)}>
                {t('common.cancel')}
              </Button>
              <Button
                variant="filled"
                color="blue"
                loading={saving}
                disabled={
                  editor.username.trim().length === 0 ||
                  (editor.account === null && editor.password.length === 0)
                }
                onClick={() => submitEditor(editor)}
              >
                {editor.account ? t('common.save') : t('user.accounts.actions.create')}
              </Button>
            </div>
          </div>
        </Modal>
      )}

      {confirmation && (
        <ConfirmationModal
          opened
          onClose={() => setConfirmation(null)}
          onConfirm={() => runConfirmation(confirmation)}
          loading={busyAccountId === confirmation.account.id}
          title={
            confirmation.kind === 'delete'
              ? t('user.accounts.confirm.deleteTitle')
              : t('user.accounts.confirm.roleTitle')
          }
          confirmLabel={
            confirmation.kind === 'delete'
              ? t('common.delete')
              : t('user.accounts.confirm.roleLabel')
          }
          confirmColor={confirmation.kind === 'delete' ? 'red' : 'yellow'}
        >
          <p className="text-sm text-themed-secondary">
            {confirmation.kind === 'delete'
              ? t('user.accounts.confirm.deleteBody', { username: confirmation.account.username })
              : t('user.accounts.confirm.roleBody', {
                  username: confirmation.account.username,
                  role:
                    confirmation.account.role === 'admin'
                      ? t('user.accounts.roles.user')
                      : t('user.accounts.roles.admin')
                })}
          </p>
        </ConfirmationModal>
      )}
    </>
  );
};

export default UserAccounts;

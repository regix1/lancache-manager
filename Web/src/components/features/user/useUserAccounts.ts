import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { useMockMode } from '@contexts/useMockMode';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useTimeoutCallback } from '@hooks/useTimeoutCallback';
import { useManagerLoading } from '@hooks/useManagerLoading';
import { getErrorMessage } from '@utils/error';
import { API_BASE } from '@utils/constants';
import type { AccountEditor, UserAccount } from './types';

// Someone working through several accounts sends one event per row, close together, and
// the list is reloaded whole either way, so the burst collapses into one reload.
const RELOAD_DEBOUNCE_MS = 1000;

export interface UserAccountsState {
  accounts: UserAccount[];
  loading: boolean;
  loadError: string | null;
  /** True once one load finished without error; a later failure never clears it. */
  loaded: boolean;
  reload: () => Promise<void>;
  /** Creates (editor.account null) or edits an account. Throws the server's refusal. */
  saveAccount: (editor: AccountEditor) => Promise<void>;
  /** Reports its own failure; a repeat call for an account whose request is out is ignored. */
  setAccountDisabled: (account: UserAccount, disabled: boolean) => void;
  /** Throws the server's refusal. */
  deleteAccount: (account: UserAccount) => Promise<void>;
  /** Account whose disable or enable request is out, or null. */
  disablingAccountId: string | null;
}

/**
 * Reads and writes the accounts users sign in with.
 *
 * Nothing here decides who may do what. The account rows a caller is answered with, and every
 * refusal, come from the server, so every account runs the same screen and sees whatever its own
 * session is entitled to.
 */
export const useUserAccounts = (): UserAccountsState => {
  const { t } = useTranslation();
  const { notifyError } = useErrorHandler();
  const { mockMode } = useMockMode();
  const { on, off, isConnected } = useSignalR();

  const [accounts, setAccounts] = useState<UserAccount[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  // Starts loading so the first paint shows the skeleton, never the empty state.
  const {
    isLoading: loading,
    hasInitiallyLoaded: loaded,
    setLoading,
    markLoaded,
    markFailed
  } = useManagerLoading(true);
  const [disablingAccountId, setDisablingAccountId] = useState<string | null>(null);

  const scheduleReload = useTimeoutCallback(RELOAD_DEBOUNCE_MS);
  const accountsRequestRef = useRef(0);
  // Account ids whose disable or enable request is out: a repeat click on that account is ignored,
  // another account's click still sends.
  const setDisabledInProgressRef = useRef(new Set<string>());

  /**
   * @param firstLoad True for the load that fills an empty screen: it shows the loading state while
   * the request is out. A reload behind somebody else's change passes false, because rows that are
   * still correct are worse blanked than left a moment stale; a failure there shows the error box
   * above the rows and leaves them standing.
   */
  const loadAccounts = useCallback(
    async (firstLoad: boolean) => {
      // Mount, reconnect, account events and Retry can overlap; only the newest request writes
      // the rows, the error or the loading state.
      const request = ++accountsRequestRef.current;
      // These are real accounts. Mock mode shows none rather than listing them on a screen the
      // reader turned on to see generated data.
      if (mockMode) {
        setAccounts([]);
        setLoadError(null);
        markLoaded();
        return;
      }
      try {
        if (firstLoad) {
          setLoading(true);
        }
        const response = await fetch(`${API_BASE}/accounts`, ApiService.getFetchOptions({}));
        const loadedAccounts = await ApiService.handleResponse<UserAccount[]>(response);
        if (request !== accountsRequestRef.current) return;
        setAccounts(loadedAccounts);
        setLoadError(null);
        markLoaded();
      } catch (err: unknown) {
        if (request !== accountsRequestRef.current) return;
        // With no rows yet the box replaces the empty state, so a request that never arrived is
        // not reported as "no accounts"; with rows it sits above them.
        setLoadError(getErrorMessage(err));
        markFailed();
      }
    },
    [mockMode, setLoading, markLoaded, markFailed]
  );

  useEffect(() => {
    void loadAccounts(true);
  }, [loadAccounts]);

  // Changes made while the socket was down are never delivered, so a genuine reconnect reloads.
  useReconnectRefetch(isConnected, () => {
    void loadAccounts(false);
  });

  useEffect(() => {
    const handleAccountsChanged = () => {
      scheduleReload(() => {
        void loadAccounts(false);
      });
    };

    on('AccountsChanged', handleAccountsChanged);
    // A sign-in stamps the account's last sign-in time and announces only the new session.
    on('UserSessionCreated', handleAccountsChanged);

    return () => {
      off('AccountsChanged', handleAccountsChanged);
      off('UserSessionCreated', handleAccountsChanged);
    };
  }, [on, off, loadAccounts, scheduleReload]);

  const reload = useCallback(() => loadAccounts(true), [loadAccounts]);

  const saveAccount = async (open: AccountEditor): Promise<void> => {
    const url = open.account ? `${API_BASE}/accounts/${open.account.id}` : `${API_BASE}/accounts`;
    const body = open.account
      ? // An edit that leaves the password blank is a rename: null tells the server to keep the
        // stored password rather than replace it with an empty one.
        { username: open.username, password: open.password ? open.password : null }
      : { username: open.username, password: open.password };

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
  };

  const setAccountDisabled = async (account: UserAccount, disabled: boolean): Promise<void> => {
    if (setDisabledInProgressRef.current.has(account.id)) {
      return;
    }
    setDisabledInProgressRef.current.add(account.id);
    setDisablingAccountId(account.id);
    try {
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
      setDisabledInProgressRef.current.delete(account.id);
      // A click on another account while this one was out owns the busy mark now.
      setDisablingAccountId((current: string | null) => (current === account.id ? null : current));
    }
  };

  const deleteAccount = async (account: UserAccount): Promise<void> => {
    const response = await fetch(
      `${API_BASE}/accounts/${account.id}`,
      ApiService.getFetchOptions({ method: 'DELETE' })
    );
    await ApiService.handleResponse<{ message: string }>(response);
    setAccounts((prev: UserAccount[]) =>
      prev.filter((existing: UserAccount) => existing.id !== account.id)
    );
  };

  return {
    accounts,
    loading,
    loadError,
    loaded,
    reload,
    saveAccount,
    setAccountDisabled: (account: UserAccount, disabled: boolean) =>
      void setAccountDisabled(account, disabled),
    deleteAccount,
    disablingAccountId
  };
};

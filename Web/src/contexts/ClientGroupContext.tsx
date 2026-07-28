import React, { useState, useEffect, useCallback, useMemo, useRef, type ReactNode } from 'react';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type { SignalREventName } from '@contexts/SignalRContext/types';
import { useLoadLifecycle } from '@hooks/useLoadLifecycle';
import { useManagerLoading } from '@hooks/useManagerLoading';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useTimeoutCallback } from '@hooks/useTimeoutCallback';
import { ClientGroupContext, type ClientGroupContextType } from './ClientGroupContext.types';
import type {
  ClientGroup,
  CreateClientGroupRequest,
  CreateClientGroupResult,
  SetMembersResult,
  UpdateClientGroupRequest,
  UpdateClientGroupResult
} from '../types';

interface ClientGroupProviderProps {
  children: ReactNode;
}

/** Successive group writes can land within a moment of each other, so the burst collapses into one load. */
const REFRESH_DEBOUNCE_MS = 1000;

/** A cleared list is handled on its own: the empty result is already known and needs no request. */
const CLIENT_GROUP_EVENTS: readonly SignalREventName[] = [
  'ClientGroupCreated',
  'ClientGroupUpdated',
  'ClientGroupDeleted'
];

/**
 * Every load builds a fresh array, so an unchanged list would still hand each consumer a new
 * identity and re-render every downloads row. Keeping the previous array when nothing moved holds
 * `getGroupForIp` and the provider value stable across a routine refetch. [13]
 */
const sameClientGroups = (previous: ClientGroup[], next: ClientGroup[]): boolean => {
  if (previous.length !== next.length) return false;
  return previous.every((group, index) => {
    const candidate = next[index];
    return (
      group.id === candidate.id &&
      group.nickname === candidate.nickname &&
      group.description === candidate.description &&
      group.separateMemberRows === candidate.separateMemberRows &&
      group.createdAtUtc === candidate.createdAtUtc &&
      group.updatedAtUtc === candidate.updatedAtUtc &&
      group.memberIps.length === candidate.memberIps.length &&
      group.memberIps.every((ip, ipIndex) => ip === candidate.memberIps[ipIndex])
    );
  });
};

export const ClientGroupProvider: React.FC<ClientGroupProviderProps> = ({ children }) => {
  const { authMode, isLoading: authLoading } = useAuth();
  const { on, off, isConnected } = useSignalR();
  const { isLoading, beginLoad, markLoaded, markFailed } = useManagerLoading();
  const [clientGroups, setClientGroups] = useState<ClientGroup[]>([]);
  const scheduleReload = useTimeoutCallback(REFRESH_DEBOUNCE_MS);
  const authModeRef = useRef(authMode);
  // When the newest load that is still trusted began. A load reads state the server has already
  // committed, so anything that changed before this instant is covered by it.
  const loadStartedAtRef = useRef(0);

  const { load, error, reset } = useLoadLifecycle<ClientGroup[]>({
    canLoad: () => authModeRef.current === 'authenticated' || authModeRef.current === 'guest',
    request: (signal: AbortSignal) => ApiService.getClientGroups(signal),
    onStarted: () => {
      loadStartedAtRef.current = Date.now();
      // Only a list that has never painted may be swapped for a spinner; every later load leaves
      // the rows on screen. [11]
      beginLoad(true);
    },
    onLoaded: (groups: ClientGroup[]) => {
      setClientGroups((previous) => (sameClientGroups(previous, groups) ? previous : groups));
    },
    onFailed: (err: unknown, owned: boolean) => {
      // The previous rows stay on screen, so a pending event may no longer keep vouching for
      // them; the next caller has to retry.
      if (owned) loadStartedAtRef.current = 0;
      console.error('Failed to fetch client groups:', err);
    },
    onSettled: (loaded: boolean) => {
      // A failed first load must stay un-painted, so the next attempt may show the spinner again
      // instead of leaving an empty list with no explanation.
      if (loaded) markLoaded();
      else markFailed();
    }
  });

  // Load nickname mapping for any signed-in viewer (admin or guest). Guests need this for
  // ClientIpDisplay / RetroView labels; mutations stay AdminOnly server-side.
  useEffect(() => {
    // Written here rather than in its own effect so its ordering against the load below is defined.
    authModeRef.current = authMode;
    if (authLoading) return;
    if (authMode === 'authenticated' || authMode === 'guest') {
      void load(false);
    } else {
      reset();
      loadStartedAtRef.current = 0;
      setClientGroups([]);
      // No load can complete without a session, so the flags must not stay raised.
      markFailed();
    }
  }, [authLoading, authMode, load, reset, markFailed]);

  // Stable, so a consumer can depend on it without its effect re-running for the context's own
  // reasons. It only asks to be current, which a recent load already satisfies.
  const refreshGroups = useCallback(async (): Promise<void> => {
    await load(false);
  }, [load]);

  // A mutation made here is already committed server-side, so its rows reload at once instead of
  // waiting out the debounce the matching echo is held by.
  const reloadAfterChange = useCallback(async (): Promise<void> => {
    await load(true);
  }, [load]);

  // CRUD operations
  const createClientGroup = useCallback(
    async (data: CreateClientGroupRequest): Promise<CreateClientGroupResult> => {
      const created = await ApiService.createClientGroup(data);
      // A rejection reloads too: the addresses that were refused belong to nicknames this list
      // may not know about yet, and the picker has to show who holds them.
      await reloadAfterChange();
      return created;
    },
    [reloadAfterChange]
  );

  const updateClientGroup = useCallback(
    async (id: number, data: UpdateClientGroupRequest): Promise<UpdateClientGroupResult> => {
      const updated = await ApiService.updateClientGroup(id, data);
      // A refused write reloads too: it was refused precisely because this list is behind.
      await reloadAfterChange();
      return updated;
    },
    [reloadAfterChange]
  );

  const deleteClientGroup = useCallback(
    async (id: number): Promise<void> => {
      await ApiService.deleteClientGroup(id);
      await reloadAfterChange();
    },
    [reloadAfterChange]
  );

  const setMembers = useCallback(
    async (
      groupId: number,
      clientIps: string[],
      expectedUpdatedAtUtc: string | null
    ): Promise<SetMembersResult> => {
      const result = await ApiService.setClientGroupMembers(
        groupId,
        clientIps,
        expectedUpdatedAtUtc
      );
      // A refused save reloads too: it was refused precisely because this list is behind.
      await reloadAfterChange();
      return result;
    },
    [reloadAfterChange]
  );

  // Helper functions
  const getGroupForIp = useCallback(
    (clientIp: string): ClientGroup | null => {
      return clientGroups.find((g) => g.memberIps.includes(clientIp)) || null;
    },
    [clientGroups]
  );

  const getGroupById = useCallback(
    (id: number): ClientGroup | null => {
      return clientGroups.find((g) => g.id === id) || null;
    },
    [clientGroups]
  );

  // Events raised while the socket was down are never delivered, which is exactly what the
  // freshness window cannot account for, so a genuine reconnect forces a reload.
  useReconnectRefetch(isConnected, () => {
    void load(true);
  });

  // Listen for SignalR events - refresh for any signed-in viewer so guest nicknames stay current
  // when an admin edits groups.
  useEffect(() => {
    const hasSession = () =>
      authModeRef.current === 'authenticated' || authModeRef.current === 'guest';

    const handleGroupsChanged = () => {
      if (!hasSession()) return;
      const changedAt = Date.now();
      scheduleReload(() => {
        // A change made in this tab is already covered by the reload its own request fired, so
        // its echo would fetch the same rows a second time. [10]
        if (loadStartedAtRef.current > changedAt) return;
        void load(true);
      });
    };

    // A database reset deletes every group, so the empty result is already known and needs no
    // refetch. Skipping the request also avoids querying the database while the reset is still
    // deleting the remaining tables. No admin gate: emptying the list is correct for any viewer.
    const handleGroupsCleared = () => {
      setClientGroups([]);
    };

    CLIENT_GROUP_EVENTS.forEach((eventName) => on(eventName, handleGroupsChanged));
    on('ClientGroupsCleared', handleGroupsCleared);

    return () => {
      CLIENT_GROUP_EVENTS.forEach((eventName) => off(eventName, handleGroupsChanged));
      off('ClientGroupsCleared', handleGroupsCleared);
    };
  }, [on, off, scheduleReload, load]);

  const value: ClientGroupContextType = useMemo(
    () => ({
      clientGroups,
      loading: isLoading,
      error,
      createClientGroup,
      updateClientGroup,
      deleteClientGroup,
      setMembers,
      refreshGroups,
      getGroupForIp,
      getGroupById
    }),
    [
      clientGroups,
      isLoading,
      error,
      createClientGroup,
      updateClientGroup,
      deleteClientGroup,
      setMembers,
      refreshGroups,
      getGroupForIp,
      getGroupById
    ]
  );

  return <ClientGroupContext.Provider value={value}>{children}</ClientGroupContext.Provider>;
};

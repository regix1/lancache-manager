import React, { useState, useEffect, useCallback, useRef, type ReactNode } from 'react';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { ClientGroupContext } from './ClientGroupContext.types';
import type { ClientGroup, CreateClientGroupRequest, UpdateClientGroupRequest } from '../types';

interface ClientGroupProviderProps {
  children: ReactNode;
}

export const ClientGroupProvider: React.FC<ClientGroupProviderProps> = ({ children }) => {
  const { authMode, isLoading: authLoading } = useAuth();
  const { on, off } = useSignalR();
  const [clientGroups, setClientGroups] = useState<ClientGroup[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const refreshGroupsRef = useRef<(() => Promise<void>) | undefined>(undefined);

  // Fetch all client groups
  const refreshGroups = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const groups = await ApiService.getClientGroups();
      setClientGroups(groups);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to fetch client groups';
      setError(message);
      console.error('Failed to fetch client groups:', err);
    } finally {
      setLoading(false);
    }
  }, []);

  // Load nickname mapping for any signed-in viewer (admin or guest). Guests need
  // this for ClientIpDisplay / RetroView labels; mutations stay AdminOnly server-side.
  useEffect(() => {
    if (authLoading) return;
    if (authMode === 'authenticated' || authMode === 'guest') {
      refreshGroups();
    } else {
      setClientGroups([]);
    }
  }, [authLoading, authMode, refreshGroups]);

  // CRUD operations
  const createClientGroup = useCallback(
    async (data: CreateClientGroupRequest): Promise<ClientGroup> => {
      const created = await ApiService.createClientGroup(data);
      await refreshGroups();
      return created;
    },
    [refreshGroups]
  );

  const updateClientGroup = useCallback(
    async (id: number, data: UpdateClientGroupRequest): Promise<ClientGroup> => {
      const updated = await ApiService.updateClientGroup(id, data);
      await refreshGroups();
      return updated;
    },
    [refreshGroups]
  );

  const deleteClientGroup = useCallback(
    async (id: number): Promise<void> => {
      await ApiService.deleteClientGroup(id);
      await refreshGroups();
    },
    [refreshGroups]
  );

  const addMember = useCallback(
    async (groupId: number, clientIp: string): Promise<ClientGroup> => {
      const updated = await ApiService.addClientGroupMember(groupId, clientIp);
      await refreshGroups();
      return updated;
    },
    [refreshGroups]
  );

  const removeMember = useCallback(
    async (groupId: number, clientIp: string): Promise<void> => {
      await ApiService.removeClientGroupMember(groupId, clientIp);
      await refreshGroups();
    },
    [refreshGroups]
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

  // Keep refs updated for SignalR handlers
  const authModeRef = useRef(authMode);
  useEffect(() => {
    authModeRef.current = authMode;
  }, [authMode]);

  useEffect(() => {
    refreshGroupsRef.current = refreshGroups;
  }, [refreshGroups]);

  // Listen for SignalR events - refresh for any signed-in viewer so guest
  // nicknames stay current when an admin edits groups.
  useEffect(() => {
    const hasSession = () =>
      authModeRef.current === 'authenticated' || authModeRef.current === 'guest';

    const handleGroupCreated = () => {
      if (!hasSession()) return;
      refreshGroupsRef.current?.();
    };

    const handleGroupUpdated = () => {
      if (!hasSession()) return;
      refreshGroupsRef.current?.();
    };

    const handleGroupDeleted = () => {
      if (!hasSession()) return;
      refreshGroupsRef.current?.();
    };

    const handleMemberAdded = () => {
      if (!hasSession()) return;
      refreshGroupsRef.current?.();
    };

    const handleMemberRemoved = () => {
      if (!hasSession()) return;
      refreshGroupsRef.current?.();
    };

    // A database reset deletes every group, so the empty result is already known and needs no
    // refetch. Skipping the request also avoids querying the database while the reset is still
    // deleting the remaining tables. No admin gate: emptying the list is correct for any viewer.
    const handleGroupsCleared = () => {
      setClientGroups([]);
    };

    on('ClientGroupCreated', handleGroupCreated);
    on('ClientGroupUpdated', handleGroupUpdated);
    on('ClientGroupDeleted', handleGroupDeleted);
    on('ClientGroupMemberAdded', handleMemberAdded);
    on('ClientGroupMemberRemoved', handleMemberRemoved);
    on('ClientGroupsCleared', handleGroupsCleared);

    return () => {
      off('ClientGroupCreated', handleGroupCreated);
      off('ClientGroupUpdated', handleGroupUpdated);
      off('ClientGroupDeleted', handleGroupDeleted);
      off('ClientGroupMemberAdded', handleMemberAdded);
      off('ClientGroupMemberRemoved', handleMemberRemoved);
      off('ClientGroupsCleared', handleGroupsCleared);
    };
  }, [on, off]);

  return (
    <ClientGroupContext.Provider
      value={{
        clientGroups,
        loading,
        error,
        createClientGroup,
        updateClientGroup,
        deleteClientGroup,
        addMember,
        removeMember,
        refreshGroups,
        getGroupForIp,
        getGroupById
      }}
    >
      {children}
    </ClientGroupContext.Provider>
  );
};

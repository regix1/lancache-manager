import React, { createContext, useContext, useState, useEffect, useCallback, useRef, type ReactNode } from 'react';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/AuthContext';
import { useSignalR } from '@contexts/SignalRContext';
import type { ClientGroup, CreateClientGroupRequest, UpdateClientGroupRequest } from '../types';

interface ClientGroupContextType {
  // Client group data
  clientGroups: ClientGroup[];

  // Loading/error states
  loading: boolean;
  error: string | null;

  // CRUD operations
  createClientGroup: (data: CreateClientGroupRequest) => Promise<ClientGroup>;
  updateClientGroup: (id: number, data: UpdateClientGroupRequest) => Promise<ClientGroup>;
  deleteClientGroup: (id: number) => Promise<void>;
  addMember: (groupId: number, clientIp: string) => Promise<ClientGroup>;
  removeMember: (groupId: number, clientIp: string) => Promise<void>;
  refreshGroups: () => Promise<void>;

  // Helper functions
  getGroupForIp: (clientIp: string) => ClientGroup | null;
  getGroupById: (id: number) => ClientGroup | null;
}

const ClientGroupContext = createContext<ClientGroupContextType | undefined>(undefined);

export const useClientGroups = () => {
  const context = useContext(ClientGroupContext);
  if (!context) {
    throw new Error('useClientGroups must be used within a ClientGroupProvider');
  }
  return context;
};

interface ClientGroupProviderProps {
  children: ReactNode;
}

export const ClientGroupProvider: React.FC<ClientGroupProviderProps> = ({ children }) => {
  const { hasSession, isLoading: authLoading } = useAuth();
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

  // Initial load - only fetch when authenticated
  useEffect(() => {
    if (!authLoading && hasSession) {
      refreshGroups();
    }
  }, [authLoading, hasSession, refreshGroups]);

  // CRUD operations
  const createClientGroup = useCallback(async (data: CreateClientGroupRequest): Promise<ClientGroup> => {
    const created = await ApiService.createClientGroup(data);
    await refreshGroups();
    return created;
  }, [refreshGroups]);

  const updateClientGroup = useCallback(async (id: number, data: UpdateClientGroupRequest): Promise<ClientGroup> => {
    const updated = await ApiService.updateClientGroup(id, data);
    await refreshGroups();
    return updated;
  }, [refreshGroups]);

  const deleteClientGroup = useCallback(async (id: number): Promise<void> => {
    await ApiService.deleteClientGroup(id);
    await refreshGroups();
  }, [refreshGroups]);

  const addMember = useCallback(async (groupId: number, clientIp: string): Promise<ClientGroup> => {
    const updated = await ApiService.addClientGroupMember(groupId, clientIp);
    await refreshGroups();
    return updated;
  }, [refreshGroups]);

  const removeMember = useCallback(async (groupId: number, clientIp: string): Promise<void> => {
    await ApiService.removeClientGroupMember(groupId, clientIp);
    await refreshGroups();
  }, [refreshGroups]);

  // Helper functions
  const getGroupForIp = useCallback((clientIp: string): ClientGroup | null => {
    return clientGroups.find(g => g.memberIps.includes(clientIp)) || null;
  }, [clientGroups]);

  const getGroupById = useCallback((id: number): ClientGroup | null => {
    return clientGroups.find(g => g.id === id) || null;
  }, [clientGroups]);

  // Keep ref updated for SignalR handlers
  useEffect(() => {
    refreshGroupsRef.current = refreshGroups;
  }, [refreshGroups]);

  // Listen for SignalR events
  useEffect(() => {
    const handleGroupCreated = () => {
      refreshGroupsRef.current?.();
    };

    const handleGroupUpdated = () => {
      refreshGroupsRef.current?.();
    };

    const handleGroupDeleted = () => {
      refreshGroupsRef.current?.();
    };

    const handleMemberAdded = () => {
      refreshGroupsRef.current?.();
    };

    const handleMemberRemoved = () => {
      refreshGroupsRef.current?.();
    };

    on('ClientGroupCreated', handleGroupCreated);
    on('ClientGroupUpdated', handleGroupUpdated);
    on('ClientGroupDeleted', handleGroupDeleted);
    on('ClientGroupMemberAdded', handleMemberAdded);
    on('ClientGroupMemberRemoved', handleMemberRemoved);

    return () => {
      off('ClientGroupCreated', handleGroupCreated);
      off('ClientGroupUpdated', handleGroupUpdated);
      off('ClientGroupDeleted', handleGroupDeleted);
      off('ClientGroupMemberAdded', handleMemberAdded);
      off('ClientGroupMemberRemoved', handleMemberRemoved);
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

import { createContext } from 'react';
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

export const ClientGroupContext = createContext<ClientGroupContextType | undefined>(undefined);

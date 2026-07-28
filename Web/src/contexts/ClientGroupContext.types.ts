import { createContext } from 'react';
import type {
  ClientGroup,
  CreateClientGroupRequest,
  CreateClientGroupResult,
  SetMembersResult,
  UpdateClientGroupRequest,
  UpdateClientGroupResult
} from '../types';

export interface ClientGroupContextType {
  // Client group data
  clientGroups: ClientGroup[];

  // Loading/error states
  /** True only until the first list has painted. Later reloads keep the rows on screen. */
  loading: boolean;
  error: string | null;

  // CRUD operations
  createClientGroup: (data: CreateClientGroupRequest) => Promise<CreateClientGroupResult>;
  /**
   * Saves the nickname's own fields. `data.expectedUpdatedAtUtc` is the copy the editor started
   * from: this is the first write a save makes and it moves the group's stamp, so a nickname
   * someone else changed since is refused here instead of overwritten.
   */
  updateClientGroup: (
    id: number,
    data: UpdateClientGroupRequest
  ) => Promise<UpdateClientGroupResult>;
  deleteClientGroup: (id: number) => Promise<void>;
  /**
   * Replaces the whole membership. `expectedUpdatedAtUtc` is the copy the editor started from, so
   * a nickname changed by someone else since then is refused instead of overwritten; null asks for
   * no precondition.
   */
  setMembers: (
    groupId: number,
    clientIps: string[],
    expectedUpdatedAtUtc: string | null
  ) => Promise<SetMembersResult>;
  refreshGroups: () => Promise<void>;

  // Helper functions
  getGroupForIp: (clientIp: string) => ClientGroup | null;
  getGroupById: (id: number) => ClientGroup | null;
}

export const ClientGroupContext = createContext<ClientGroupContextType | undefined>(undefined);

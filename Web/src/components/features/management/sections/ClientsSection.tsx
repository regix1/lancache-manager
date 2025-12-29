import React, { useState, useMemo } from 'react';
import { Card, CardHeader, CardTitle, CardContent } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { useClientGroups } from '@contexts/ClientGroupContext';
import { useStats } from '@contexts/StatsContext';
import { Plus, Users, Trash2, Edit2, X, Loader2 } from 'lucide-react';
import ClientGroupModal from '@components/modals/ClientGroupModal';
import type { ClientGroup } from '../../../../types';

interface ClientsSectionProps {
  isAuthenticated: boolean;
  authMode: string;
  mockMode: boolean;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
}

const ClientsSection: React.FC<ClientsSectionProps> = ({
  isAuthenticated,
  onError,
  onSuccess
}) => {
  const {
    clientGroups,
    loading,
    deleteClientGroup,
    removeMember
  } = useClientGroups();

  const { clientStats } = useStats();

  const [isModalOpen, setIsModalOpen] = useState(false);
  const [editingGroup, setEditingGroup] = useState<ClientGroup | null>(null);
  const [deletingGroupId, setDeletingGroupId] = useState<number | null>(null);
  const [removingMember, setRemovingMember] = useState<{ groupId: number; ip: string } | null>(null);

  // Get all IPs that are in groups
  const groupedIps = useMemo(() => {
    const ips = new Set<string>();
    clientGroups.forEach(g => g.memberIps.forEach(ip => ips.add(ip)));
    return ips;
  }, [clientGroups]);

  // Get ungrouped clients (IPs from stats that aren't in any group)
  const ungroupedClients = useMemo(() => {
    return clientStats
      .filter(stat => !stat.isGrouped && !groupedIps.has(stat.clientIp))
      .map(stat => stat.clientIp);
  }, [clientStats, groupedIps]);

  const handleCreateGroup = () => {
    setEditingGroup(null);
    setIsModalOpen(true);
  };

  const handleEditGroup = (group: ClientGroup) => {
    setEditingGroup(group);
    setIsModalOpen(true);
  };

  const handleDeleteGroup = async (group: ClientGroup) => {
    if (!window.confirm(`Are you sure you want to delete the group "${group.nickname}"? The IPs will become ungrouped.`)) {
      return;
    }

    setDeletingGroupId(group.id);
    try {
      await deleteClientGroup(group.id);
      onSuccess(`Deleted group "${group.nickname}"`);
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Failed to delete group');
    } finally {
      setDeletingGroupId(null);
    }
  };

  const handleRemoveMember = async (groupId: number, ip: string, nickname: string) => {
    setRemovingMember({ groupId, ip });
    try {
      await removeMember(groupId, ip);
      onSuccess(`Removed ${ip} from "${nickname}"`);
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Failed to remove member');
    } finally {
      setRemovingMember(null);
    }
  };

  const handleModalClose = () => {
    setIsModalOpen(false);
    setEditingGroup(null);
  };

  const handleModalSuccess = (message: string) => {
    onSuccess(message);
    handleModalClose();
  };

  return (
    <div
      className="management-section animate-fade-in"
      role="tabpanel"
      id="panel-clients"
      aria-labelledby="tab-clients"
    >
      {/* Section Header */}
      <div className="mb-6 flex items-center justify-between">
        <div>
          <h2 className="text-xl font-semibold text-themed-primary mb-1">
            Client Management
          </h2>
          <p className="text-themed-secondary text-sm">
            Assign nicknames to clients and group multiple IPs under one identity
          </p>
        </div>
        {isAuthenticated && (
          <Button
            onClick={handleCreateGroup}
            className="flex items-center gap-2"
          >
            <Plus className="w-4 h-4" />
            Create Group
          </Button>
        )}
      </div>

      {/* Client Groups */}
      <div className="mb-8">
        <div className="flex items-center gap-2 mb-4">
          <div
            className="w-1 h-5 rounded-full"
            style={{ backgroundColor: 'var(--theme-primary)' }}
          />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            Client Groups ({clientGroups.length})
          </h3>
        </div>

        {loading ? (
          <Card>
            <CardContent className="py-8 flex items-center justify-center">
              <Loader2 className="w-6 h-6 animate-spin text-themed-muted" />
              <span className="ml-2 text-themed-muted">Loading client groups...</span>
            </CardContent>
          </Card>
        ) : clientGroups.length === 0 ? (
          <Card>
            <CardContent className="py-8 text-center text-themed-muted">
              <Users className="w-12 h-12 mx-auto mb-3 opacity-50" />
              <p className="mb-2">No client groups created yet</p>
              <p className="text-sm">
                Create a group to assign a nickname and consolidate multiple client IPs
              </p>
            </CardContent>
          </Card>
        ) : (
          <div className="grid gap-4">
            {clientGroups.map(group => (
              <Card key={group.id}>
                <CardHeader className="flex flex-row items-center justify-between py-3">
                  <div className="flex items-center gap-3">
                    <div
                      className="w-10 h-10 rounded-lg flex items-center justify-center"
                      style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      <Users className="w-5 h-5 text-themed-muted" />
                    </div>
                    <div>
                      <CardTitle className="text-base">{group.nickname}</CardTitle>
                      {group.description && (
                        <p className="text-sm text-themed-muted">{group.description}</p>
                      )}
                    </div>
                  </div>
                  {isAuthenticated && (
                    <div className="flex items-center gap-2">
                      <Button
                        variant="subtle"
                        size="sm"
                        onClick={() => handleEditGroup(group)}
                        title="Edit group"
                      >
                        <Edit2 className="w-4 h-4" />
                      </Button>
                      <Button
                        variant="subtle"
                        size="sm"
                        onClick={() => handleDeleteGroup(group)}
                        disabled={deletingGroupId === group.id}
                        title="Delete group"
                        className="text-red-500 hover:text-red-600"
                      >
                        {deletingGroupId === group.id ? (
                          <Loader2 className="w-4 h-4 animate-spin" />
                        ) : (
                          <Trash2 className="w-4 h-4" />
                        )}
                      </Button>
                    </div>
                  )}
                </CardHeader>
                <CardContent className="pt-0 pb-4">
                  <div className="text-sm text-themed-secondary mb-2">
                    {group.memberIps.length} {group.memberIps.length === 1 ? 'IP' : 'IPs'}
                  </div>
                  <div className="flex flex-wrap gap-2">
                    {group.memberIps.map(ip => (
                      <div
                        key={ip}
                        className="flex items-center gap-1 px-2 py-1 rounded text-sm font-mono"
                        style={{
                          backgroundColor: 'var(--theme-bg-tertiary)',
                          color: 'var(--theme-text-secondary)'
                        }}
                      >
                        <span>{ip}</span>
                        {isAuthenticated && (
                          <button
                            onClick={() => handleRemoveMember(group.id, ip, group.nickname)}
                            disabled={removingMember?.groupId === group.id && removingMember?.ip === ip}
                            className="ml-1 p-0.5 rounded hover:bg-red-500/20 text-themed-muted hover:text-red-500 transition-colors"
                            title="Remove from group"
                          >
                            {removingMember?.groupId === group.id && removingMember?.ip === ip ? (
                              <Loader2 className="w-3 h-3 animate-spin" />
                            ) : (
                              <X className="w-3 h-3" />
                            )}
                          </button>
                        )}
                      </div>
                    ))}
                  </div>
                </CardContent>
              </Card>
            ))}
          </div>
        )}
      </div>

      {/* Ungrouped Clients */}
      {ungroupedClients.length > 0 && (
        <div>
          <div className="flex items-center gap-2 mb-4">
            <div
              className="w-1 h-5 rounded-full"
              style={{ backgroundColor: 'var(--theme-icon-orange)' }}
            />
            <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
              Ungrouped Clients ({ungroupedClients.length})
            </h3>
          </div>

          <Card>
            <CardContent className="py-4">
              <p className="text-sm text-themed-muted mb-3">
                These IPs don't belong to any group. Create or edit a group to add them.
              </p>
              <div className="flex flex-wrap gap-2">
                {ungroupedClients.slice(0, 20).map(ip => (
                  <div
                    key={ip}
                    className="px-2 py-1 rounded text-sm font-mono"
                    style={{
                      backgroundColor: 'var(--theme-bg-tertiary)',
                      color: 'var(--theme-text-muted)'
                    }}
                  >
                    {ip}
                  </div>
                ))}
                {ungroupedClients.length > 20 && (
                  <div
                    className="px-2 py-1 rounded text-sm"
                    style={{ color: 'var(--theme-text-muted)' }}
                  >
                    +{ungroupedClients.length - 20} more
                  </div>
                )}
              </div>
            </CardContent>
          </Card>
        </div>
      )}

      {/* Modal */}
      <ClientGroupModal
        isOpen={isModalOpen}
        onClose={handleModalClose}
        group={editingGroup}
        ungroupedIps={ungroupedClients}
        onSuccess={handleModalSuccess}
        onError={onError}
      />
    </div>
  );
};

export default ClientsSection;

import React, { useState, useMemo, useEffect } from 'react';
import { Card, CardHeader, CardTitle, CardContent } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Tooltip } from '@components/ui/Tooltip';
import { Modal } from '@components/ui/Modal';
import { Alert } from '@components/ui/Alert';
import { Pagination } from '@components/ui/Pagination';
import { useClientGroups } from '@contexts/ClientGroupContext';
import ApiService from '@services/api.service';
import { Plus, Users, Trash2, Edit2, X, Loader2, User, AlertTriangle } from 'lucide-react';
import ClientGroupModal from '@components/modals/ClientGroupModal';
import type { ClientGroup } from '../../../../types';

const UNGROUPED_IPS_PER_PAGE = 20;

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

  // Fetch ALL client IPs without time filtering - management sections should not be affected by time filters
  const [allClientIps, setAllClientIps] = useState<string[]>([]);
  const [loadingClients, setLoadingClients] = useState(true);

  useEffect(() => {
    let cancelled = false;
    const fetchAllClients = async () => {
      try {
        // Call getClientStats without time params to get all clients ever seen
        const stats = await ApiService.getClientStats();
        if (!cancelled) {
          const ips = stats.map(stat => stat.clientIp);
          setAllClientIps(ips);
        }
      } catch (err) {
        console.error('Failed to fetch all client IPs:', err);
      } finally {
        if (!cancelled) {
          setLoadingClients(false);
        }
      }
    };
    fetchAllClients();
    return () => { cancelled = true; };
  }, []);

  const [isModalOpen, setIsModalOpen] = useState(false);
  const [editingGroup, setEditingGroup] = useState<ClientGroup | null>(null);
  const [deletingGroupId, setDeletingGroupId] = useState<number | null>(null);
  const [removingMember, setRemovingMember] = useState<{ groupId: number; ip: string } | null>(null);
  const [deleteConfirmGroup, setDeleteConfirmGroup] = useState<ClientGroup | null>(null);
  const [ungroupedPage, setUngroupedPage] = useState(1);

  // Get all IPs that are in groups
  const groupedIps = useMemo(() => {
    const ips = new Set<string>();
    clientGroups.forEach(g => g.memberIps.forEach(ip => ips.add(ip)));
    return ips;
  }, [clientGroups]);

  // Get ungrouped clients (IPs that aren't in any group)
  const ungroupedClients = useMemo(() => {
    return allClientIps.filter(ip => !groupedIps.has(ip));
  }, [allClientIps, groupedIps]);

  // Pagination for ungrouped clients
  const totalUngroupedPages = Math.ceil(ungroupedClients.length / UNGROUPED_IPS_PER_PAGE);
  const paginatedUngroupedClients = useMemo(() => {
    const startIndex = (ungroupedPage - 1) * UNGROUPED_IPS_PER_PAGE;
    return ungroupedClients.slice(startIndex, startIndex + UNGROUPED_IPS_PER_PAGE);
  }, [ungroupedClients, ungroupedPage]);

  const handleCreateGroup = () => {
    setEditingGroup(null);
    setIsModalOpen(true);
  };

  const handleEditGroup = (group: ClientGroup) => {
    setEditingGroup(group);
    setIsModalOpen(true);
  };

  const handleDeleteGroup = (group: ClientGroup) => {
    setDeleteConfirmGroup(group);
  };

  const confirmDeleteGroup = async () => {
    if (!deleteConfirmGroup) return;

    setDeletingGroupId(deleteConfirmGroup.id);
    try {
      await deleteClientGroup(deleteConfirmGroup.id);
      onSuccess(`Deleted nickname "${deleteConfirmGroup.nickname}"`);
      setDeleteConfirmGroup(null);
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Failed to delete nickname');
    } finally {
      setDeletingGroupId(null);
    }
  };

  const handleRemoveMember = async (groupId: number, ip: string, nickname: string) => {
    setRemovingMember({ groupId, ip });
    try {
      await removeMember(groupId, ip);
      onSuccess(`Removed ${ip} from nickname "${nickname}"`);
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Failed to remove IP');
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
            Client Nicknames
          </h2>
          <p className="text-themed-secondary text-sm">
            Assign friendly names to client IPs for easier identification
          </p>
        </div>
        {isAuthenticated && (
          <Button
            onClick={handleCreateGroup}
            className="flex items-center gap-2"
          >
            <Plus className="w-4 h-4" />
            Add Nickname
          </Button>
        )}
      </div>

      {/* Client Nicknames */}
      <div className="mb-8">
        <div className="flex items-center gap-2 mb-4">
          <div
            className="w-1 h-5 rounded-full"
            style={{ backgroundColor: 'var(--theme-primary)' }}
          />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            Nicknames ({clientGroups.length})
          </h3>
        </div>

        {loading ? (
          <Card>
            <CardContent className="py-8 flex items-center justify-center">
              <Loader2 className="w-6 h-6 animate-spin text-themed-muted" />
              <span className="ml-2 text-themed-muted">Loading nicknames...</span>
            </CardContent>
          </Card>
        ) : clientGroups.length === 0 ? (
          <Card>
            <CardContent className="py-8 text-center text-themed-muted">
              <User className="w-12 h-12 mx-auto mb-3 opacity-50" />
              <p className="mb-2">No nicknames assigned yet</p>
              <p className="text-sm">
                Add a nickname to give a friendly name to client IPs
              </p>
            </CardContent>
          </Card>
        ) : (
          <div className="grid gap-4">
            {clientGroups.map(group => {
              const isMultiIp = group.memberIps.length > 1;
              return (
                <Card key={group.id}>
                  <CardHeader className="flex flex-row items-center justify-between py-3">
                    <div className="flex items-center gap-3">
                      <div
                        className="w-10 h-10 rounded-lg flex items-center justify-center"
                        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                      >
                        {isMultiIp ? (
                          <Users className="w-5 h-5 text-themed-muted" />
                        ) : (
                          <User className="w-5 h-5 text-themed-muted" />
                        )}
                      </div>
                      <div>
                        <div className="flex items-center gap-2">
                          <CardTitle className="text-base">{group.nickname}</CardTitle>
                          {isMultiIp && (
                            <Tooltip content="This nickname is shared by multiple IPs">
                              <span
                                className="flex items-center gap-1 px-1.5 py-0.5 rounded text-xs"
                                style={{
                                  backgroundColor: 'color-mix(in srgb, var(--theme-icon-orange) 15%, transparent)',
                                  color: 'var(--theme-icon-orange)'
                                }}
                              >
                                <AlertTriangle className="w-3 h-3" />
                                {group.memberIps.length} IPs
                              </span>
                            </Tooltip>
                          )}
                        </div>
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
                          title="Edit nickname"
                        >
                          <Edit2 className="w-4 h-4" />
                        </Button>
                        <Button
                          variant="subtle"
                          size="sm"
                          color="red"
                          onClick={() => handleDeleteGroup(group)}
                          disabled={deletingGroupId === group.id}
                          title="Delete nickname"
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
                              onClick={(e) => {
                                e.stopPropagation();
                                handleRemoveMember(group.id, ip, group.nickname);
                              }}
                              disabled={removingMember?.groupId === group.id && removingMember?.ip === ip}
                              className="ml-1 p-0.5 rounded text-themed-muted delete-hover"
                              title="Remove IP"
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
              );
            })}
          </div>
        )}
      </div>

      {/* Clients Without Nicknames */}
      {(loadingClients || ungroupedClients.length > 0) && (
        <div>
          <div className="flex items-center gap-2 mb-4">
            <div
              className="w-1 h-5 rounded-full"
              style={{ backgroundColor: 'var(--theme-icon-orange)' }}
            />
            <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
              Without Nicknames {!loadingClients && `(${ungroupedClients.length})`}
            </h3>
          </div>

          <Card>
            <CardContent className="py-4">
              {loadingClients ? (
                <div className="flex items-center justify-center py-4">
                  <Loader2 className="w-5 h-5 animate-spin text-themed-muted" />
                  <span className="ml-2 text-themed-muted">Loading clients...</span>
                </div>
              ) : (
                <>
                  <p className="text-sm text-themed-muted mb-3">
                    These client IPs don't have nicknames assigned. Click "Add Nickname" above to give them friendly names.
                  </p>
                  <div className="flex flex-wrap gap-2">
                    {paginatedUngroupedClients.map(ip => (
                      <Tooltip key={ip} content={`Click "Add Nickname" to name this IP`}>
                        <div
                          className="px-2 py-1 rounded text-sm font-mono cursor-help"
                          style={{
                            backgroundColor: 'var(--theme-bg-tertiary)',
                            color: 'var(--theme-text-muted)'
                          }}
                        >
                          {ip}
                        </div>
                      </Tooltip>
                    ))}
                  </div>
                </>
              )}
              {!loadingClients && totalUngroupedPages > 1 && (
                <Pagination
                  currentPage={ungroupedPage}
                  totalPages={totalUngroupedPages}
                  totalItems={ungroupedClients.length}
                  itemsPerPage={UNGROUPED_IPS_PER_PAGE}
                  onPageChange={setUngroupedPage}
                  itemLabel="IPs"
                  showCard={false}
                  compact
                  className="mt-3"
                />
              )}
            </CardContent>
          </Card>
        </div>
      )}

      {/* Edit/Create Modal */}
      <ClientGroupModal
        isOpen={isModalOpen}
        onClose={handleModalClose}
        group={editingGroup}
        ungroupedIps={ungroupedClients}
        onSuccess={handleModalSuccess}
        onError={onError}
      />

      {/* Delete Confirmation Modal */}
      <Modal
        opened={deleteConfirmGroup !== null}
        onClose={() => {
          if (deletingGroupId === null) {
            setDeleteConfirmGroup(null);
          }
        }}
        title={
          <div className="flex items-center gap-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Delete Nickname</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Delete nickname <strong>{deleteConfirmGroup?.nickname}</strong>? The associated IPs will no longer have a nickname.
          </p>

          <Alert color="yellow">
            <p className="text-sm">This action cannot be undone.</p>
          </Alert>

          <div className="flex justify-end gap-3 pt-2">
            <Button
              variant="default"
              onClick={() => setDeleteConfirmGroup(null)}
              disabled={deletingGroupId !== null}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={confirmDeleteGroup}
              loading={deletingGroupId !== null}
            >
              Delete
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
};

export default ClientsSection;

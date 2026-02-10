import React, { useState, useMemo, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Card, CardHeader, CardTitle, CardContent } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Tooltip } from '@components/ui/Tooltip';
import { Modal } from '@components/ui/Modal';
import { Alert } from '@components/ui/Alert';
import { Pagination } from '@components/ui/Pagination';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { useClientGroups } from '@contexts/ClientGroupContext';
import { useStats, useDownloads } from '@contexts/DashboardDataContext';
import ApiService from '@services/api.service';
import { Plus, Users, Trash2, Edit2, X, Loader2, User, AlertTriangle } from 'lucide-react';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
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
  const { t } = useTranslation();
  const {
    clientGroups,
    loading,
    deleteClientGroup,
    removeMember
  } = useClientGroups();
  const { refreshStats } = useStats();
  const { refreshDownloads } = useDownloads();

  // Fetch ALL client IPs without time filtering - management sections should not be affected by time filters
  const [allClientIps, setAllClientIps] = useState<string[]>([]);
  const [loadingClients, setLoadingClients] = useState(true);

  useEffect(() => {
    let cancelled = false;
    const fetchAllClients = async () => {
      try {
        // Call getClientStats without time params to get all clients ever seen
        const stats = await ApiService.getClientStats(undefined, undefined, undefined, undefined, true);
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

  const [excludedIps, setExcludedIps] = useState<string[]>([]);
  const [savedExcludedIps, setSavedExcludedIps] = useState<string[]>([]);
  const [excludeInput, setExcludeInput] = useState('');
  const [selectedKnownIps, setSelectedKnownIps] = useState<string[]>([]);
  const [loadingExcluded, setLoadingExcluded] = useState(false);
  const [savingExcluded, setSavingExcluded] = useState(false);

  const hasExcludedChanges = useMemo(() => (
    excludedIps.join('|') !== savedExcludedIps.join('|')
  ), [excludedIps, savedExcludedIps]);

  const loadExcludedIps = useCallback(async () => {
    if (!isAuthenticated) return;
    setLoadingExcluded(true);
    try {
      const response = await ApiService.getStatsExclusions();
      const ips = response.ips || [];
      setExcludedIps(ips);
      setSavedExcludedIps(ips);
    } catch (err) {
      onError(err instanceof Error ? err.message : t('management.sections.clients.errors.failedToLoadExcluded'));
    } finally {
      setLoadingExcluded(false);
    }
  }, [isAuthenticated, onError]);

  useEffect(() => {
    loadExcludedIps();
  }, [loadExcludedIps]);

  const isValidIpv4 = (value: string) => {
    const parts = value.split('.');
    if (parts.length !== 4) return false;
    return parts.every(part => {
      if (!/^\d{1,3}$/.test(part)) return false;
      const num = Number(part);
      return num >= 0 && num <= 255;
    });
  };

  const isValidIpv6 = (value: string) => {
    if (!value.includes(':')) return false;
    if (!/^[0-9a-fA-F:]+$/.test(value)) return false;
    const parts = value.split(':');
    if (parts.length < 3 || parts.length > 8) return false;
    return parts.every(part => part.length <= 4);
  };

  const parseIpCandidates = useCallback((input: string) => (
    input
      .split(/[,\s]+/)
      .map(ip => ip.trim())
      .filter(Boolean)
  ), []);

  const invalidInputIps = useMemo(() => (
    parseIpCandidates(excludeInput).filter(ip => !isValidIpv4(ip) && !isValidIpv6(ip))
  ), [excludeInput, parseIpCandidates]);

  const handleAddExcluded = () => {
    const candidates = parseIpCandidates(excludeInput);
    const validCandidates = candidates.filter(ip => isValidIpv4(ip) || isValidIpv6(ip));

    if (validCandidates.length === 0) return;

    setExcludedIps((prev) => {
      const next = [...prev];
      for (const ip of validCandidates) {
        if (!next.includes(ip)) {
          next.push(ip);
        }
      }
      return next;
    });

    setExcludeInput('');
  };

  const handleAddKnownIps = () => {
    if (selectedKnownIps.length === 0) return;
    setExcludedIps((prev) => {
      const next = [...prev];
      for (const ip of selectedKnownIps) {
        if (!next.includes(ip)) {
          next.push(ip);
        }
      }
      return next;
    });
    setSelectedKnownIps([]);
  };

  const handleRemoveExcluded = (ip: string) => {
    setExcludedIps((prev) => prev.filter(item => item !== ip));
  };

  const handleSaveExcluded = async () => {
    setSavingExcluded(true);
    try {
      const response = await ApiService.updateStatsExclusions(excludedIps);
      const ips = response.ips || [];
      setExcludedIps(ips);
      setSavedExcludedIps(ips);
      onSuccess(t('management.sections.clients.excludedIpsUpdated'));
      await refreshStats(true);
      await refreshDownloads();
    } catch (err) {
      onError(err instanceof Error ? err.message : t('management.sections.clients.errors.failedToUpdateExcluded'));
    } finally {
      setSavingExcluded(false);
    }
  };

  const nicknameByIp = useMemo(() => {
    const map = new Map<string, string>();
    clientGroups.forEach(group => {
      group.memberIps.forEach(ip => map.set(ip, group.nickname));
    });
    return map;
  }, [clientGroups]);

  const knownClientOptions = useMemo(() => {
    const excludedSet = new Set(excludedIps);
    return allClientIps
      .filter(ip => !excludedSet.has(ip))
      .sort((a, b) => a.localeCompare(b))
      .map(ip => {
        const nickname = nicknameByIp.get(ip);
        return {
          value: ip,
          label: nickname ? `${nickname} (${ip})` : ip
        };
      });
  }, [allClientIps, excludedIps, nicknameByIp]);

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
      onSuccess(t('management.sections.clients.deletedNickname', { nickname: deleteConfirmGroup.nickname }));
      setDeleteConfirmGroup(null);
    } catch (err) {
      onError(err instanceof Error ? err.message : t('modals.clientGroup.errors.failedToDelete'));
    } finally {
      setDeletingGroupId(null);
    }
  };

  const handleRemoveMember = async (groupId: number, ip: string, nickname: string) => {
    setRemovingMember({ groupId, ip });
    try {
      await removeMember(groupId, ip);
      onSuccess(t('management.sections.clients.removedIpFromNickname', { ip, nickname }));
    } catch (err) {
      onError(err instanceof Error ? err.message : t('modals.clientGroup.errors.failedToRemoveIp'));
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
            {t('management.sections.clients.title')}
          </h2>
          <p className="text-themed-secondary text-sm">
            {t('management.sections.clients.subtitle')}
          </p>
        </div>
        {isAuthenticated && (
          <Button
            onClick={handleCreateGroup}
            className="flex items-center gap-2"
          >
            <Plus className="w-4 h-4" />
            {t('management.sections.clients.addNickname')}
          </Button>
        )}
      </div>

      {/* Client Nicknames */}
      <div className="mb-8">
        <div className="flex items-center gap-2 mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-primary)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.clients.nicknames')}
            {clientGroups.length > 0 && <span className="count-badge">{clientGroups.length}</span>}
          </h3>
        </div>

        {loading ? (
          <Card>
            <CardContent className="py-8 flex items-center justify-center">
              <Loader2 className="w-6 h-6 animate-spin text-themed-muted" />
              <span className="ml-2 text-themed-muted">{t('management.sections.clients.loadingNicknames')}</span>
            </CardContent>
          </Card>
        ) : clientGroups.length === 0 ? (
          <Card>
            <CardContent className="py-8 text-center text-themed-muted">
              <User className="w-12 h-12 mx-auto mb-3 opacity-50" />
              <p className="mb-2">{t('management.sections.clients.noNicknamesYet')}</p>
              <p className="text-sm">
                {t('management.sections.clients.noNicknamesDesc')}
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
                      <div className="w-10 h-10 rounded-lg flex items-center justify-center bg-themed-tertiary">
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
                            <Tooltip content={t('modals.clientGroup.multiIpWarning')}>
                              <span
                              className="flex items-center gap-1 px-1.5 py-0.5 rounded text-xs icon-bg-orange icon-orange"
                              >
                                <AlertTriangle className="w-3 h-3" />
                                {group.memberIps.length} {t('management.sections.clients.ipsLabel')}
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
                          title={t('common.edit')}
                        >
                          <Edit2 className="w-4 h-4" />
                        </Button>
                        <Button
                          variant="subtle"
                          size="sm"
                          color="red"
                          onClick={() => handleDeleteGroup(group)}
                          disabled={deletingGroupId === group.id}
                          title={t('common.delete')}
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
                          className="flex items-center gap-1 px-2 py-1 rounded text-sm font-mono bg-themed-tertiary text-themed-secondary"
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
                              title={t('management.sections.clients.removeIp')}
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
            <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-orange)]" />
            <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
              {t('management.sections.clients.withoutNicknames')}
              {!loadingClients && ungroupedClients.length > 0 && <span className="count-badge">{ungroupedClients.length}</span>}
            </h3>
          </div>

          <Card>
            <CardContent className="py-4">
              {loadingClients ? (
                <div className="flex items-center justify-center py-4">
                  <Loader2 className="w-5 h-5 animate-spin text-themed-muted" />
                  <span className="ml-2 text-themed-muted">{t('management.sections.clients.loadingClients')}</span>
                </div>
              ) : (
                <>
                  <p className="text-sm text-themed-muted mb-3">
                    {t('management.sections.clients.withoutNicknamesDesc')}
                  </p>
                  <div className="flex flex-wrap gap-2">
                    {paginatedUngroupedClients.map(ip => (
                      <Tooltip key={ip} content={t('management.sections.clients.clickAddNicknameTooltip')}>
                        <div className="px-2 py-1 rounded text-sm font-mono cursor-help bg-themed-tertiary text-themed-muted">
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
                  itemLabel={t('management.sections.clients.ipsLabel')}
                  showCard={false}
                  compact
                  className="mt-3"
                />
              )}
            </CardContent>
          </Card>
        </div>
      )}

      {/* Exclude IPs from Stats */}
      <div className="mt-8">
        <div className="flex items-center gap-2 mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-red)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.clients.excludeFromStats')}
          </h3>
        </div>

        <Card>
          <CardContent className="py-5 space-y-4">
            <div className="text-sm text-themed-secondary">
              {t('management.sections.clients.excludedIpsDesc')}
            </div>

            {!isAuthenticated ? (
              <Alert color="yellow">
                <span className="text-sm">{t('management.sections.clients.authenticateToManage')}</span>
              </Alert>
            ) : (
              <>
                <div className="space-y-3">

                  <div className="text-xs text-themed-muted uppercase tracking-wide font-semibold">
                    {t('management.sections.clients.pickFromKnownClients')}
                  </div>
                  <div className="flex flex-col sm:flex-row sm:items-center gap-3">
                    <MultiSelectDropdown
                      options={knownClientOptions}
                      values={selectedKnownIps}
                      onChange={setSelectedKnownIps}
                      placeholder={t('management.sections.clients.selectClients')}
                      minSelections={0}
                      disabled={loadingExcluded || savingExcluded || knownClientOptions.length === 0}
                      className="w-full"
                    />
                    <Button
                      onClick={handleAddKnownIps}
                      variant="filled"
                      color="blue"
                      className="sm:w-40"
                      disabled={selectedKnownIps.length === 0 || loadingExcluded || savingExcluded}
                    >
                      {t('management.sections.clients.addSelected')}
                    </Button>
                  </div>
                  {knownClientOptions.length === 0 && (
                    <div className="text-sm text-themed-muted">
                      {t('management.sections.clients.allKnownExcluded')}
                    </div>
                  )}
                </div>

                <div className="border-t border-themed-primary pt-4" />

                <div className="flex flex-col sm:flex-row sm:items-center gap-3">
                  <input
                    type="text"
                    value={excludeInput}
                    onChange={(e) => setExcludeInput(e.target.value)}
                    placeholder={t('management.sections.clients.addIpsPlaceholder')}
                    className="w-full px-3 py-2 rounded-lg transition-colors
                             bg-themed-secondary text-themed-primary
                             border border-themed-secondary focus:border-themed-focus
                             placeholder:text-themed-muted"
                    disabled={loadingExcluded || savingExcluded}
                  />
                  <Button
                    onClick={handleAddExcluded}
                    variant="filled"
                    color="blue"
                    className="sm:w-32"
                    disabled={loadingExcluded || savingExcluded || excludeInput.trim().length === 0 || invalidInputIps.length > 0}
                  >
                    {t('management.sections.clients.add')}
                  </Button>
                </div>
                {excludeInput.trim().length > 0 && (
                  <div className="text-xs text-themed-muted">
                    {t('management.sections.clients.supportsIpv4Ipv6')}
                  </div>
                )}
                {invalidInputIps.length > 0 && (
                  <Alert color="yellow">
                    <span className="text-sm">
                      {t('management.sections.clients.invalidIps', { ips: invalidInputIps.join(', ') })}
                    </span>
                  </Alert>
                )}

                {loadingExcluded ? (
                  <div className="flex items-center gap-2 text-themed-muted text-sm">
                    <Loader2 className="w-4 h-4 animate-spin" />
                    {t('management.sections.clients.loadingExcludedIps')}
                  </div>
                ) : excludedIps.length === 0 ? (
                  <div className="text-sm text-themed-muted">
                    {t('management.sections.clients.noExcludedIps')}
                  </div>
                ) : (
                  <div className="flex flex-col gap-2">
                    {excludedIps.map(ip => (
                      <div
                        key={ip}
                        className="flex items-center justify-between p-3 rounded-lg bg-themed-tertiary"
                      >
                        <div className="flex items-center gap-3">
                          <div className="font-mono text-themed-secondary font-medium">
                            <ClientIpDisplay clientIp={ip} showTooltip={false} />
                          </div>
                        </div>

                        <div className="flex items-center gap-3">
                          <Button
                            variant="subtle"
                            size="sm"
                            color="red"
                            className="text-themed-muted hover:text-red-500"
                            onClick={() => handleRemoveExcluded(ip)}
                            disabled={savingExcluded}
                            title={t('management.sections.clients.removeIp')}
                          >
                            <Trash2 className="w-4 h-4" />
                          </Button>
                        </div>
                      </div>
                    ))}
                  </div>
                )}

                <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 pt-2">
                  <div />
                  <Button
                    onClick={handleSaveExcluded}
                    disabled={!hasExcludedChanges || savingExcluded || loadingExcluded}
                    loading={savingExcluded}
                    className="sm:w-40"
                  >
                    {t('management.sections.clients.saveChanges')}
                  </Button>
                </div>
              </>
            )}
          </CardContent>
        </Card>
      </div>

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
            <span>{t('management.sections.clients.deleteNickname')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.sections.clients.deleteNicknameConfirm', { nickname: deleteConfirmGroup?.nickname })}
          </p>

          <Alert color="yellow">
            <p className="text-sm">{t('management.sections.clients.deleteNicknameWarning')}</p>
          </Alert>

          <div className="flex justify-end gap-3 pt-2">
            <Button
              variant="default"
              onClick={() => setDeleteConfirmGroup(null)}
              disabled={deletingGroupId !== null}
            >
              {t('common.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={confirmDeleteGroup}
              loading={deletingGroupId !== null}
            >
              {t('management.sections.clients.delete')}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
};

export default ClientsSection;

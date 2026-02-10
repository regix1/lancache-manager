import React, { useState, useCallback, useEffect, useMemo } from 'react';
import { Plus, X, Loader2, AlertTriangle } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Pagination } from '@components/ui/Pagination';
import { useClientGroups } from '@contexts/ClientGroupContext';
import { useTranslation } from 'react-i18next';
import type { ClientGroup } from '../../types';

const IPS_PER_PAGE = 20;

interface ClientGroupModalProps {
  isOpen: boolean;
  onClose: () => void;
  group: ClientGroup | null; // null for create, ClientGroup for edit
  ungroupedIps: string[];
  onSuccess: (message: string) => void;
  onError?: (message: string) => void; // Optional, errors shown inline in modal
}

const ClientGroupModal: React.FC<ClientGroupModalProps> = ({
  isOpen,
  onClose,
  group,
  ungroupedIps,
  onSuccess
}) => {
  const { t } = useTranslation();
  const { createClientGroup, updateClientGroup, addMember } = useClientGroups();
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Form state
  const [nickname, setNickname] = useState('');
  const [description, setDescription] = useState('');
  const [selectedIps, setSelectedIps] = useState<string[]>([]); // For create mode
  const [pendingIps, setPendingIps] = useState<string[]>([]); // For edit mode - IPs to add on save
  const [ipSearchQuery, setIpSearchQuery] = useState('');
  const [ipPage, setIpPage] = useState(1);

  // Reset form when modal opens/closes or group changes
  useEffect(() => {
    if (isOpen) {
      if (group) {
        setNickname(group.nickname);
        setDescription(group.description || '');
        setSelectedIps([]);
        setPendingIps([]);
      } else {
        setNickname('');
        setDescription('');
        setSelectedIps([]);
        setPendingIps([]);
      }
      setError(null);
      setIpSearchQuery('');
      setIpPage(1);
    }
  }, [isOpen, group]);

  // Reset page when search query changes
  useEffect(() => {
    setIpPage(1);
  }, [ipSearchQuery]);

  const isEditing = group !== null;

  // Filter available IPs based on search query
  const filteredIps = useMemo(() =>
    ungroupedIps.filter(ip =>
      ip.toLowerCase().includes(ipSearchQuery.toLowerCase())
    ),
    [ungroupedIps, ipSearchQuery]
  );

  // Pagination calculations for available IPs (excluding already selected/pending)
  const availableIpsForCreate = useMemo(() =>
    filteredIps.filter(ip => !selectedIps.includes(ip)),
    [filteredIps, selectedIps]
  );

  const availableIpsForEdit = useMemo(() =>
    filteredIps.filter(ip => !pendingIps.includes(ip)),
    [filteredIps, pendingIps]
  );

  const totalPagesCreate = Math.ceil(availableIpsForCreate.length / IPS_PER_PAGE);
  const totalPagesEdit = Math.ceil(availableIpsForEdit.length / IPS_PER_PAGE);

  const paginatedIpsForCreate = useMemo(() => {
    const startIndex = (ipPage - 1) * IPS_PER_PAGE;
    return availableIpsForCreate.slice(startIndex, startIndex + IPS_PER_PAGE);
  }, [availableIpsForCreate, ipPage]);

  const paginatedIpsForEdit = useMemo(() => {
    const startIndex = (ipPage - 1) * IPS_PER_PAGE;
    return availableIpsForEdit.slice(startIndex, startIndex + IPS_PER_PAGE);
  }, [availableIpsForEdit, ipPage]);

  const handleAddIp = (ip: string) => {
    if (!selectedIps.includes(ip)) {
      setSelectedIps([...selectedIps, ip]);
    }
    setIpSearchQuery('');
  };

  const handleRemoveIp = (ip: string) => {
    setSelectedIps(selectedIps.filter(i => i !== ip));
  };

  // Edit mode: Add IP to pending list (saved on submit)
  const handleAddPendingIp = (ip: string) => {
    if (!pendingIps.includes(ip)) {
      setPendingIps([...pendingIps, ip]);
    }
    setIpSearchQuery('');
  };

  // Edit mode: Remove IP from pending list
  const handleRemovePendingIp = (ip: string) => {
    setPendingIps(pendingIps.filter(i => i !== ip));
  };

  const handleSubmit = useCallback(async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    if (!nickname.trim()) {
      setError(t('modals.clientGroup.errors.nicknameRequired'));
      return;
    }

    setSaving(true);
    try {
      if (isEditing) {
        // Update nickname/description
        await updateClientGroup(group.id, {
          nickname: nickname.trim(),
          description: description.trim() || undefined
        });
        // Add any pending IPs
        for (const ip of pendingIps) {
          await addMember(group.id, ip);
        }
        const ipsAdded = pendingIps.length > 0 ? t('modals.clientGroup.messages.andAddedIps', { count: pendingIps.length }) : '';
        onSuccess(t('modals.clientGroup.messages.updatedNickname', { nickname: nickname.trim(), ipsAdded }));
      } else {
        await createClientGroup({
          nickname: nickname.trim(),
          description: description.trim() || undefined,
          initialIps: selectedIps.length > 0 ? selectedIps : undefined
        });
        onSuccess(t('modals.clientGroup.messages.addedNickname', { nickname: nickname.trim() }));
      }
      onClose();
    } catch (err) {
      const message = err instanceof Error ? err.message : t('modals.clientGroup.errors.failedToSave');
      setError(message);
    } finally {
      setSaving(false);
    }
  }, [nickname, description, selectedIps, pendingIps, isEditing, group, createClientGroup, updateClientGroup, addMember, onClose, onSuccess]);

  if (!isOpen) return null;

  return (
    <Modal
      opened={isOpen}
      onClose={onClose}
      title={isEditing ? t('modals.clientGroup.editTitle') : t('modals.clientGroup.addTitle')}
    >
      <form onSubmit={handleSubmit}>
        {/* Error Alert */}
        {error && (
          <div className="mb-4 p-3 rounded-lg text-sm bg-error text-error-text border border-error">
            {error}
          </div>
        )}

        {/* Multi-IP Warning */}
        {!isEditing && selectedIps.length > 1 && (
          <div className="mb-4 p-3 rounded-lg text-sm flex items-start gap-2 bg-warning text-warning-text border border-warning">
            <AlertTriangle className="w-4 h-4 flex-shrink-0 mt-0.5" />
            <div>
              <strong>{t('modals.clientGroup.warnings.multipleIpsTitle')}</strong>
              <p className="text-themed-secondary text-xs mt-1">
                {t('modals.clientGroup.warnings.multipleIpsDesc', { count: selectedIps.length })}
              </p>
            </div>
          </div>
        )}

        {/* Multi-IP Warning for editing */}
        {isEditing && group && group.memberIps.length > 1 && (
          <div className="mb-4 p-3 rounded-lg text-sm flex items-start gap-2 bg-warning text-warning-text border border-warning">
            <AlertTriangle className="w-4 h-4 flex-shrink-0 mt-0.5" />
            <div>
              <strong>{t('modals.clientGroup.warnings.sharedNicknameTitle')}</strong>
              <p className="text-themed-secondary text-xs mt-1">
                {t('modals.clientGroup.warnings.sharedNicknameDesc', { count: group.memberIps.length })}
              </p>
            </div>
          </div>
        )}

        {/* Nickname */}
        <div className="mb-4">
          <label
            htmlFor="nickname"
            className="block text-sm font-medium text-themed-secondary mb-1"
          >
            {t('modals.clientGroup.labels.nickname')} <span className="text-themed-error">*</span>
          </label>
          <input
            id="nickname"
            type="text"
            value={nickname}
            onChange={(e) => setNickname(e.target.value)}
            className="w-full px-3 py-2 rounded-lg border text-themed-primary themed-input"
            placeholder={t('modals.clientGroup.placeholders.name')}
            required
            autoFocus
          />
        </div>

        {/* Description */}
        <div className="mb-4">
          <label
            htmlFor="description"
            className="block text-sm font-medium text-themed-secondary mb-1"
          >
            {t('modals.clientGroup.labels.description')} <span className="text-themed-muted">({t('modals.clientGroup.labels.optional')})</span>
          </label>
          <textarea
            id="description"
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            className="w-full px-3 py-2 rounded-lg border text-themed-primary resize-none themed-input"
            placeholder={t('modals.clientGroup.placeholders.description')}
            rows={2}
          />
        </div>

        {/* IP Selection (Create mode or Edit mode add more) */}
        {isEditing ? (
          // Edit mode: Show current members and option to add more
          <div className="mb-4">
            <label className="block text-sm font-medium text-themed-secondary mb-2">
              {t('modals.clientGroup.labels.currentIps')} <span className="count-badge">{group.memberIps.length}</span>
            </label>
            <div className="flex flex-wrap gap-2 mb-3">
              {group.memberIps.map(ip => (
                <div
                  key={ip}
                  className="px-2 py-1 rounded text-sm font-mono bg-themed-tertiary text-themed-secondary"
                >
                  {ip}
                </div>
              ))}
            </div>

            {/* Pending IPs to be added */}
            {pendingIps.length > 0 && (
              <>
                <label className="block text-sm font-medium text-themed-secondary mb-2">
                  {t('modals.clientGroup.labels.ipsToAdd')} <span className="count-badge">{pendingIps.length}</span>
                </label>
                <div className="flex flex-wrap gap-2 mb-3">
                  {pendingIps.map(ip => (
                    <div
                      key={ip}
                      className="flex items-center gap-1 px-2 py-1 rounded text-sm font-mono bg-primary text-themed-button"
                    >
                      {ip}
                      <button
                        type="button"
                        onClick={() => handleRemovePendingIp(ip)}
                        className="hover-btn-light p-0.5 rounded"
                      >
                        <X className="w-3 h-3" />
                      </button>
                    </div>
                  ))}
                </div>
              </>
            )}

            {ungroupedIps.filter(ip => !pendingIps.includes(ip)).length > 0 && (
              <>
                <label className="block text-sm font-medium text-themed-secondary mb-2">
                  {t('modals.clientGroup.labels.addMoreIps')}
                </label>
                <div className="mb-2">
                  <input
                    type="text"
                    value={ipSearchQuery}
                    onChange={(e) => setIpSearchQuery(e.target.value)}
                    className="w-full px-3 py-2 rounded-lg border text-themed-primary text-sm themed-input"
                    placeholder={t('modals.clientGroup.placeholders.searchUngrouped')}
                  />
                </div>
                <div className="max-h-32 overflow-y-auto rounded-lg border p-2 bg-themed-tertiary border-themed-primary">
                  {availableIpsForEdit.length === 0 ? (
                    <p className="text-sm text-themed-muted text-center py-2">
                      {ipSearchQuery ? t('modals.clientGroup.emptyStates.noMatchingIps') : t('modals.clientGroup.emptyStates.noUngroupedIps')}
                    </p>
                  ) : (
                    <>
                      <div className="flex flex-wrap gap-2">
                        {paginatedIpsForEdit.map(ip => (
                          <button
                            key={ip}
                            type="button"
                            onClick={() => handleAddPendingIp(ip)}
                            className="flex items-center gap-1 px-2 py-1 rounded text-sm font-mono transition-colors hover:bg-opacity-80 bg-themed-secondary text-themed-secondary"
                          >
                            <Plus className="w-3 h-3" />
                            {ip}
                          </button>
                        ))}
                      </div>
                      {totalPagesEdit > 1 && (
                        <Pagination
                          currentPage={ipPage}
                          totalPages={totalPagesEdit}
                          totalItems={availableIpsForEdit.length}
                          itemsPerPage={IPS_PER_PAGE}
                          onPageChange={setIpPage}
                          itemLabel="IPs"
                          showCard={false}
                          compact
                        />
                      )}
                    </>
                  )}
                </div>
              </>
            )}
          </div>
        ) : (
          // Create mode: Select initial IPs
          <div className="mb-4">
            <label className="block text-sm font-medium text-themed-secondary mb-2">
              {t('modals.clientGroup.labels.clientIps')} <span className="text-themed-muted">({t('modals.clientGroup.labels.selectAtLeastOne')})</span>
            </label>

            {/* Selected IPs */}
            {selectedIps.length > 0 && (
              <div className="flex flex-wrap gap-2 mb-2">
                {selectedIps.map(ip => (
                  <div
                    key={ip}
                    className="flex items-center gap-1 px-2 py-1 rounded text-sm font-mono bg-primary text-themed-button"
                  >
                    {ip}
                    <button
                      type="button"
                      onClick={() => handleRemoveIp(ip)}
                      className="hover-btn-light p-0.5 rounded"
                    >
                      <X className="w-3 h-3" />
                    </button>
                  </div>
                ))}
              </div>
            )}

            {ungroupedIps.length > 0 && (
              <>
                <div className="mb-2">
                  <input
                    type="text"
                    value={ipSearchQuery}
                    onChange={(e) => setIpSearchQuery(e.target.value)}
                    className="w-full px-3 py-2 rounded-lg border text-themed-primary text-sm themed-input"
                    placeholder={t('modals.clientGroup.placeholders.searchToAdd')}
                  />
                </div>
                <div className="max-h-32 overflow-y-auto rounded-lg border p-2 bg-themed-tertiary border-themed-primary">
                  {availableIpsForCreate.length === 0 ? (
                    <p className="text-sm text-themed-muted text-center py-2">
                      {ipSearchQuery ? t('modals.clientGroup.emptyStates.noMatchingIps') : t('modals.clientGroup.emptyStates.allSelected')}
                    </p>
                  ) : (
                    <>
                      <div className="flex flex-wrap gap-2">
                        {paginatedIpsForCreate.map(ip => (
                          <button
                            key={ip}
                            type="button"
                            onClick={() => handleAddIp(ip)}
                            className="flex items-center gap-1 px-2 py-1 rounded text-sm font-mono transition-colors hover:bg-opacity-80 bg-themed-secondary text-themed-secondary"
                          >
                            <Plus className="w-3 h-3" />
                            {ip}
                          </button>
                        ))}
                      </div>
                      {totalPagesCreate > 1 && (
                        <Pagination
                          currentPage={ipPage}
                          totalPages={totalPagesCreate}
                          totalItems={availableIpsForCreate.length}
                          itemsPerPage={IPS_PER_PAGE}
                          onPageChange={setIpPage}
                          itemLabel="IPs"
                          showCard={false}
                          compact
                        />
                      )}
                    </>
                  )}
                </div>
              </>
            )}

            {ungroupedIps.length === 0 && (
              <p className="text-sm text-themed-muted">
                {t('modals.clientGroup.emptyStates.noClientsAvailable')}
              </p>
            )}
          </div>
        )}

        {/* Actions */}
        <div className="flex justify-end gap-3 pt-4 border-t border-themed-primary">
          <Button
            type="button"
            variant="subtle"
            onClick={onClose}
            disabled={saving}
          >
            {t('common.cancel')}
          </Button>
          <Button
            type="submit"
            disabled={saving || !nickname.trim()}
          >
            {saving ? (
              <>
                <Loader2 className="w-4 h-4 animate-spin mr-2" />
                {isEditing ? t('modals.clientGroup.actions.saving') : t('modals.clientGroup.actions.adding')}
              </>
            ) : (
              isEditing ? t('modals.clientGroup.actions.saveChanges') : t('modals.clientGroup.actions.addNickname')
            )}
          </Button>
        </div>
      </form>
    </Modal>
  );
};

export default ClientGroupModal;

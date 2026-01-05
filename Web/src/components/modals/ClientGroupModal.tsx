import React, { useState, useCallback, useEffect, useMemo } from 'react';
import { Plus, X, Loader2, AlertTriangle } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Pagination } from '@components/ui/Pagination';
import { useClientGroups } from '@contexts/ClientGroupContext';
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
      setError('Nickname is required');
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
        const ipsAdded = pendingIps.length > 0 ? ` and added ${pendingIps.length} IP${pendingIps.length > 1 ? 's' : ''}` : '';
        onSuccess(`Updated nickname "${nickname.trim()}"${ipsAdded}`);
      } else {
        await createClientGroup({
          nickname: nickname.trim(),
          description: description.trim() || undefined,
          initialIps: selectedIps.length > 0 ? selectedIps : undefined
        });
        onSuccess(`Added nickname "${nickname.trim()}"`);
      }
      onClose();
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to save nickname';
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
      title={isEditing ? 'Edit Client Nickname' : 'Add Client Nickname'}
    >
      <form onSubmit={handleSubmit}>
        {/* Error Alert */}
        {error && (
          <div
            className="mb-4 p-3 rounded-lg text-sm"
            style={{
              backgroundColor: 'var(--theme-error-bg)',
              color: 'var(--theme-error-text)',
              border: '1px solid var(--theme-error)'
            }}
          >
            {error}
          </div>
        )}

        {/* Multi-IP Warning */}
        {!isEditing && selectedIps.length > 1 && (
          <div
            className="mb-4 p-3 rounded-lg text-sm flex items-start gap-2"
            style={{
              backgroundColor: 'var(--theme-warning-bg)',
              color: 'var(--theme-warning-text)',
              border: '1px solid var(--theme-warning)'
            }}
          >
            <AlertTriangle className="w-4 h-4 flex-shrink-0 mt-0.5" />
            <div>
              <strong>Multiple IPs selected</strong>
              <p className="text-themed-secondary text-xs mt-1">
                All {selectedIps.length} IPs will share the same nickname. This is typically used when the same device has multiple IP addresses.
              </p>
            </div>
          </div>
        )}

        {/* Multi-IP Warning for editing */}
        {isEditing && group && group.memberIps.length > 1 && (
          <div
            className="mb-4 p-3 rounded-lg text-sm flex items-start gap-2"
            style={{
              backgroundColor: 'var(--theme-warning-bg)',
              color: 'var(--theme-warning-text)',
              border: '1px solid var(--theme-warning)'
            }}
          >
            <AlertTriangle className="w-4 h-4 flex-shrink-0 mt-0.5" />
            <div>
              <strong>Shared nickname</strong>
              <p className="text-themed-secondary text-xs mt-1">
                This nickname is shared by {group.memberIps.length} IPs. Changes will apply to all of them.
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
            Nickname <span className="text-themed-error">*</span>
          </label>
          <input
            id="nickname"
            type="text"
            value={nickname}
            onChange={(e) => setNickname(e.target.value)}
            className="w-full px-3 py-2 rounded-lg border text-themed-primary"
            style={{
              backgroundColor: 'var(--theme-bg-secondary)',
              borderColor: 'var(--theme-border-primary)'
            }}
            placeholder="e.g., Gaming PC, Living Room, Server"
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
            Description <span className="text-themed-muted">(optional)</span>
          </label>
          <textarea
            id="description"
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            className="w-full px-3 py-2 rounded-lg border text-themed-primary resize-none"
            style={{
              backgroundColor: 'var(--theme-bg-secondary)',
              borderColor: 'var(--theme-border-primary)'
            }}
            placeholder="Optional description for this group"
            rows={2}
          />
        </div>

        {/* IP Selection (Create mode or Edit mode add more) */}
        {isEditing ? (
          // Edit mode: Show current members and option to add more
          <div className="mb-4">
            <label className="block text-sm font-medium text-themed-secondary mb-2">
              Current IPs ({group.memberIps.length})
            </label>
            <div className="flex flex-wrap gap-2 mb-3">
              {group.memberIps.map(ip => (
                <div
                  key={ip}
                  className="px-2 py-1 rounded text-sm font-mono"
                  style={{
                    backgroundColor: 'var(--theme-bg-tertiary)',
                    color: 'var(--theme-text-secondary)'
                  }}
                >
                  {ip}
                </div>
              ))}
            </div>

            {/* Pending IPs to be added */}
            {pendingIps.length > 0 && (
              <>
                <label className="block text-sm font-medium text-themed-secondary mb-2">
                  IPs to Add ({pendingIps.length})
                </label>
                <div className="flex flex-wrap gap-2 mb-3">
                  {pendingIps.map(ip => (
                    <div
                      key={ip}
                      className="flex items-center gap-1 px-2 py-1 rounded text-sm font-mono"
                      style={{
                        backgroundColor: 'var(--theme-primary)',
                        color: 'var(--theme-button-text)'
                      }}
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
                  Add More IPs
                </label>
                <div className="mb-2">
                  <input
                    type="text"
                    value={ipSearchQuery}
                    onChange={(e) => setIpSearchQuery(e.target.value)}
                    className="w-full px-3 py-2 rounded-lg border text-themed-primary text-sm"
                    style={{
                      backgroundColor: 'var(--theme-bg-secondary)',
                      borderColor: 'var(--theme-border-primary)'
                    }}
                    placeholder="Search ungrouped IPs..."
                  />
                </div>
                <div
                  className="max-h-32 overflow-y-auto rounded-lg border p-2"
                  style={{
                    backgroundColor: 'var(--theme-bg-tertiary)',
                    borderColor: 'var(--theme-border-primary)'
                  }}
                >
                  {availableIpsForEdit.length === 0 ? (
                    <p className="text-sm text-themed-muted text-center py-2">
                      {ipSearchQuery ? 'No matching IPs' : 'No ungrouped IPs available'}
                    </p>
                  ) : (
                    <>
                      <div className="flex flex-wrap gap-2">
                        {paginatedIpsForEdit.map(ip => (
                          <button
                            key={ip}
                            type="button"
                            onClick={() => handleAddPendingIp(ip)}
                            className="flex items-center gap-1 px-2 py-1 rounded text-sm font-mono transition-colors hover:bg-opacity-80"
                            style={{
                              backgroundColor: 'var(--theme-bg-secondary)',
                              color: 'var(--theme-text-secondary)'
                            }}
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
              Client IPs <span className="text-themed-muted">(select at least one)</span>
            </label>

            {/* Selected IPs */}
            {selectedIps.length > 0 && (
              <div className="flex flex-wrap gap-2 mb-2">
                {selectedIps.map(ip => (
                  <div
                    key={ip}
                    className="flex items-center gap-1 px-2 py-1 rounded text-sm font-mono"
                    style={{
                      backgroundColor: 'var(--theme-primary)',
                      color: 'var(--theme-button-text)'
                    }}
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
                    className="w-full px-3 py-2 rounded-lg border text-themed-primary text-sm"
                    style={{
                      backgroundColor: 'var(--theme-bg-secondary)',
                      borderColor: 'var(--theme-border-primary)'
                    }}
                    placeholder="Search ungrouped IPs to add..."
                  />
                </div>
                <div
                  className="max-h-32 overflow-y-auto rounded-lg border p-2"
                  style={{
                    backgroundColor: 'var(--theme-bg-tertiary)',
                    borderColor: 'var(--theme-border-primary)'
                  }}
                >
                  {availableIpsForCreate.length === 0 ? (
                    <p className="text-sm text-themed-muted text-center py-2">
                      {ipSearchQuery ? 'No matching IPs' : 'All available IPs selected'}
                    </p>
                  ) : (
                    <>
                      <div className="flex flex-wrap gap-2">
                        {paginatedIpsForCreate.map(ip => (
                          <button
                            key={ip}
                            type="button"
                            onClick={() => handleAddIp(ip)}
                            className="flex items-center gap-1 px-2 py-1 rounded text-sm font-mono transition-colors hover:bg-opacity-80"
                            style={{
                              backgroundColor: 'var(--theme-bg-secondary)',
                              color: 'var(--theme-text-secondary)'
                            }}
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
                No ungrouped clients available. You can add IPs later.
              </p>
            )}
          </div>
        )}

        {/* Actions */}
        <div className="flex justify-end gap-3 pt-4 border-t" style={{ borderColor: 'var(--theme-border-primary)' }}>
          <Button
            type="button"
            variant="subtle"
            onClick={onClose}
            disabled={saving}
          >
            Cancel
          </Button>
          <Button
            type="submit"
            disabled={saving || !nickname.trim()}
          >
            {saving ? (
              <>
                <Loader2 className="w-4 h-4 animate-spin mr-2" />
                {isEditing ? 'Saving...' : 'Adding...'}
              </>
            ) : (
              isEditing ? 'Save Changes' : 'Add Nickname'
            )}
          </Button>
        </div>
      </form>
    </Modal>
  );
};

export default ClientGroupModal;

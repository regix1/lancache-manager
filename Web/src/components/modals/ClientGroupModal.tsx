import React, { useState, useCallback, useEffect } from 'react';
import { Users, Plus, X, Loader2 } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { useClientGroups } from '@contexts/ClientGroupContext';
import type { ClientGroup } from '../../types';

interface ClientGroupModalProps {
  isOpen: boolean;
  onClose: () => void;
  group: ClientGroup | null; // null for create, ClientGroup for edit
  ungroupedIps: string[];
  onSuccess: (message: string) => void;
  onError: (message: string) => void;
}

const ClientGroupModal: React.FC<ClientGroupModalProps> = ({
  isOpen,
  onClose,
  group,
  ungroupedIps,
  onSuccess,
  onError
}) => {
  const { createClientGroup, updateClientGroup, addMember } = useClientGroups();
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [addingIp, setAddingIp] = useState<string | null>(null);

  // Form state
  const [nickname, setNickname] = useState('');
  const [description, setDescription] = useState('');
  const [selectedIps, setSelectedIps] = useState<string[]>([]);
  const [ipSearchQuery, setIpSearchQuery] = useState('');

  // Reset form when modal opens/closes or group changes
  useEffect(() => {
    if (isOpen) {
      if (group) {
        setNickname(group.nickname);
        setDescription(group.description || '');
        setSelectedIps([]);
      } else {
        setNickname('');
        setDescription('');
        setSelectedIps([]);
      }
      setError(null);
      setIpSearchQuery('');
    }
  }, [isOpen, group]);

  const isEditing = group !== null;

  // Filter available IPs based on search query
  const filteredIps = ungroupedIps.filter(ip =>
    ip.toLowerCase().includes(ipSearchQuery.toLowerCase())
  );

  const handleAddIp = (ip: string) => {
    if (!selectedIps.includes(ip)) {
      setSelectedIps([...selectedIps, ip]);
    }
    setIpSearchQuery('');
  };

  const handleRemoveIp = (ip: string) => {
    setSelectedIps(selectedIps.filter(i => i !== ip));
  };

  const handleAddIpToExistingGroup = async (ip: string) => {
    if (!group) return;

    setAddingIp(ip);
    try {
      await addMember(group.id, ip);
      onSuccess(`Added ${ip} to "${group.nickname}"`);
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Failed to add IP');
    } finally {
      setAddingIp(null);
    }
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
        await updateClientGroup(group.id, {
          nickname: nickname.trim(),
          description: description.trim() || undefined
        });
        onSuccess(`Updated group "${nickname.trim()}"`);
      } else {
        await createClientGroup({
          nickname: nickname.trim(),
          description: description.trim() || undefined,
          initialIps: selectedIps.length > 0 ? selectedIps : undefined
        });
        onSuccess(`Created group "${nickname.trim()}"`);
      }
      onClose();
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to save group';
      setError(message);
    } finally {
      setSaving(false);
    }
  }, [nickname, description, selectedIps, isEditing, group, createClientGroup, updateClientGroup, onClose, onSuccess]);

  if (!isOpen) return null;

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={isEditing ? 'Edit Client Group' : 'Create Client Group'}
    >
      <form onSubmit={handleSubmit}>
        {/* Error Alert */}
        {error && (
          <div
            className="mb-4 p-3 rounded-lg text-sm"
            style={{
              backgroundColor: 'rgba(239, 68, 68, 0.1)',
              color: 'var(--theme-text-error, #ef4444)',
              border: '1px solid rgba(239, 68, 68, 0.3)'
            }}
          >
            {error}
          </div>
        )}

        {/* Nickname */}
        <div className="mb-4">
          <label
            htmlFor="nickname"
            className="block text-sm font-medium text-themed-secondary mb-1"
          >
            Nickname <span className="text-red-500">*</span>
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
              Current Members ({group.memberIps.length})
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

            {ungroupedIps.length > 0 && (
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
                  {filteredIps.length === 0 ? (
                    <p className="text-sm text-themed-muted text-center py-2">
                      {ipSearchQuery ? 'No matching IPs' : 'No ungrouped IPs available'}
                    </p>
                  ) : (
                    <div className="flex flex-wrap gap-2">
                      {filteredIps.slice(0, 10).map(ip => (
                        <button
                          key={ip}
                          type="button"
                          onClick={() => handleAddIpToExistingGroup(ip)}
                          disabled={addingIp === ip}
                          className="flex items-center gap-1 px-2 py-1 rounded text-sm font-mono transition-colors hover:bg-opacity-80"
                          style={{
                            backgroundColor: 'var(--theme-bg-secondary)',
                            color: 'var(--theme-text-secondary)'
                          }}
                        >
                          {addingIp === ip ? (
                            <Loader2 className="w-3 h-3 animate-spin" />
                          ) : (
                            <Plus className="w-3 h-3" />
                          )}
                          {ip}
                        </button>
                      ))}
                    </div>
                  )}
                </div>
              </>
            )}
          </div>
        ) : (
          // Create mode: Select initial IPs
          <div className="mb-4">
            <label className="block text-sm font-medium text-themed-secondary mb-2">
              Initial Members <span className="text-themed-muted">(optional)</span>
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
                      className="p-0.5 rounded hover:bg-white/20 transition-colors"
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
                  {filteredIps.filter(ip => !selectedIps.includes(ip)).length === 0 ? (
                    <p className="text-sm text-themed-muted text-center py-2">
                      {ipSearchQuery ? 'No matching IPs' : 'All available IPs selected'}
                    </p>
                  ) : (
                    <div className="flex flex-wrap gap-2">
                      {filteredIps.filter(ip => !selectedIps.includes(ip)).slice(0, 10).map(ip => (
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
            variant="ghost"
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
                {isEditing ? 'Saving...' : 'Creating...'}
              </>
            ) : (
              isEditing ? 'Save Changes' : 'Create Group'
            )}
          </Button>
        </div>
      </form>
    </Modal>
  );
};

export default ClientGroupModal;

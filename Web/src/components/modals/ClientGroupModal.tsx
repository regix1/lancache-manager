import React, { useState, useCallback, useEffect, useMemo } from 'react';
import { Plus } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Pagination } from '@components/ui/Pagination';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { useClientGroups } from '@contexts/useClientGroups';
import { usePaginatedList } from '@hooks/usePaginatedList';
import { useTranslation } from 'react-i18next';
import { getErrorMessage } from '@utils/error';
import type { ClientGroup } from '../../types';
import '@components/features/management/managementSectionContent.css';
import './ClientGroupModal.css';

const IPS_PER_PAGE = 20;

interface ClientGroupModalProps {
  isOpen: boolean;
  onClose: () => void;
  group: ClientGroup | null; // null for create, ClientGroup for edit
  ungroupedIps: string[];
  /** Create mode only: IPs pre-selected when the modal opens (quick-name flow). */
  initialIps?: string[];
  onSuccess: (message: string) => void;
  onError?: (message: string) => void; // Optional, errors shown inline in modal
}

const ClientGroupModal: React.FC<ClientGroupModalProps> = ({
  isOpen,
  onClose,
  group,
  ungroupedIps,
  initialIps,
  onSuccess
}) => {
  const { t } = useTranslation();
  const { createClientGroup, updateClientGroup, addMember } = useClientGroups();
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Form state
  const [nickname, setNickname] = useState('');
  const [description, setDescription] = useState('');
  // Create mode: the group's initial IPs. Edit mode: IPs added on save.
  const [chosenIps, setChosenIps] = useState<string[]>([]);
  const [ipSearchQuery, setIpSearchQuery] = useState('');
  const [ipPage, setIpPage] = useState(1);

  // Reset form when modal opens/closes or group changes
  useEffect(() => {
    if (isOpen) {
      if (group) {
        setNickname(group.nickname);
        setDescription(group.description || '');
        setChosenIps([]);
      } else {
        setNickname('');
        setDescription('');
        setChosenIps(initialIps ?? []);
      }
      setError(null);
      setIpSearchQuery('');
      setIpPage(1);
    }
  }, [isOpen, group, initialIps]);

  // Reset page when search query changes
  useEffect(() => {
    setIpPage(1);
  }, [ipSearchQuery]);

  const isEditing = group !== null;

  // IPs still selectable: match the search and aren't chosen yet
  const availableIps = useMemo(
    () =>
      ungroupedIps.filter(
        (ip) => ip.toLowerCase().includes(ipSearchQuery.toLowerCase()) && !chosenIps.includes(ip)
      ),
    [ungroupedIps, ipSearchQuery, chosenIps]
  );

  const { paginatedItems: paginatedAvailableIps, totalPages } = usePaginatedList<string>({
    items: availableIps,
    pageSize: IPS_PER_PAGE,
    page: ipPage,
    onPageChange: setIpPage
  });

  const handleChooseIp = (ip: string) => {
    setChosenIps((prev) => (prev.includes(ip) ? prev : [...prev, ip]));
  };

  const handleUnchooseIp = (ip: string) => {
    setChosenIps((prev) => prev.filter((i) => i !== ip));
  };

  const handleSubmit = useCallback(
    async (e: React.FormEvent) => {
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
          for (const ip of chosenIps) {
            await addMember(group.id, ip);
          }
          const ipsAdded =
            chosenIps.length > 0
              ? t('modals.clientGroup.messages.andAddedIps', { count: chosenIps.length })
              : '';
          onSuccess(
            t('modals.clientGroup.messages.updatedNickname', {
              nickname: nickname.trim(),
              ipsAdded
            })
          );
        } else {
          await createClientGroup({
            nickname: nickname.trim(),
            description: description.trim() || undefined,
            initialIps: chosenIps.length > 0 ? chosenIps : undefined
          });
          onSuccess(t('modals.clientGroup.messages.addedNickname', { nickname: nickname.trim() }));
        }
        onClose();
      } catch (err) {
        setError(getErrorMessage(err) || t('modals.clientGroup.errors.failedToSave'));
      } finally {
        setSaving(false);
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [
      nickname,
      description,
      chosenIps,
      isEditing,
      group,
      createClientGroup,
      updateClientGroup,
      addMember,
      onClose,
      onSuccess
    ]
  );

  if (!isOpen) return null;

  const multiIpWarning = isEditing ? group.memberIps.length > 1 : chosenIps.length > 1;

  return (
    <Modal
      opened={isOpen}
      onClose={onClose}
      title={isEditing ? t('modals.clientGroup.editTitle') : t('modals.clientGroup.addTitle')}
    >
      <form onSubmit={handleSubmit} className="space-y-4">
        {error && (
          <Alert color="red">
            <span className="text-sm">{error}</span>
          </Alert>
        )}

        {multiIpWarning && (
          <Alert color="yellow">
            <p className="text-sm font-medium">
              {isEditing
                ? t('modals.clientGroup.warnings.sharedNicknameTitle')
                : t('modals.clientGroup.warnings.multipleIpsTitle')}
            </p>
            <p className="text-xs mt-1">
              {isEditing
                ? t('modals.clientGroup.warnings.sharedNicknameDesc', {
                    count: group.memberIps.length
                  })
                : t('modals.clientGroup.warnings.multipleIpsDesc', { count: chosenIps.length })}
            </p>
          </Alert>
        )}

        {/* Nickname */}
        <div>
          <label htmlFor="nickname" className="form-field-label">
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
        <div>
          <label htmlFor="description" className="form-field-label">
            {t('modals.clientGroup.labels.description')}{' '}
            <span className="text-themed-muted">({t('modals.clientGroup.labels.optional')})</span>
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

        {/* Current members (edit mode) */}
        {isEditing && (
          <div>
            <label className="form-field-label">
              {t('modals.clientGroup.labels.currentIps')}{' '}
              <span className="themed-badge status-badge-neutral badge-count">
                {group.memberIps.length}
              </span>
            </label>
            <div className="flex flex-wrap gap-2">
              {group.memberIps.map((ip) => (
                <div
                  key={ip}
                  className="px-2 py-1 rounded text-sm font-mono bg-themed-tertiary text-themed-secondary"
                >
                  {ip}
                </div>
              ))}
            </div>
          </div>
        )}

        {/* IP selection */}
        <div>
          <label className="form-field-label">
            {isEditing ? (
              <>
                {t('modals.clientGroup.labels.ipsToAdd')}{' '}
                {chosenIps.length > 0 && (
                  <span className="themed-badge status-badge-neutral badge-count">
                    {chosenIps.length}
                  </span>
                )}
              </>
            ) : (
              <>
                {t('modals.clientGroup.labels.clientIps')}{' '}
                <span className="text-themed-muted">
                  ({t('modals.clientGroup.labels.selectAtLeastOne')})
                </span>
              </>
            )}
          </label>

          {chosenIps.length > 0 && (
            <div className="flex flex-wrap gap-2 mb-2">
              {chosenIps.map((ip) => (
                <button
                  key={ip}
                  type="button"
                  onClick={() => handleUnchooseIp(ip)}
                  className="clientgroup-chosen-ip flex items-center gap-1 px-2 py-1 rounded text-sm font-mono bg-primary text-themed-button"
                  aria-label={t('modals.clientGroup.actions.removeChosenIp', { ip })}
                >
                  {ip}
                  <span aria-hidden="true">×</span>
                </button>
              ))}
            </div>
          )}

          {ungroupedIps.length === 0 && !isEditing ? (
            <p className="text-sm text-themed-muted">
              {t('modals.clientGroup.emptyStates.noClientsAvailable')}
            </p>
          ) : (
            availableIps.length + chosenIps.length > 0 && (
              <>
                <input
                  type="text"
                  value={ipSearchQuery}
                  onChange={(e) => setIpSearchQuery(e.target.value)}
                  className="w-full px-3 py-2 rounded-lg border text-themed-primary text-sm themed-input mb-2"
                  placeholder={
                    isEditing
                      ? t('modals.clientGroup.placeholders.searchUngrouped')
                      : t('modals.clientGroup.placeholders.searchToAdd')
                  }
                />
                <div className="mgmt-list divided-list clientgroup-ip-picker">
                  {availableIps.length === 0 ? (
                    <p className="text-sm text-themed-muted text-center py-3">
                      {ipSearchQuery
                        ? t('modals.clientGroup.emptyStates.noMatchingIps')
                        : isEditing
                          ? t('modals.clientGroup.emptyStates.noUngroupedIps')
                          : t('modals.clientGroup.emptyStates.allSelected')}
                    </p>
                  ) : (
                    <CustomScrollbar maxHeight="13.5rem" paddingMode="none" radius="none">
                      <div className="clientgroup-ip-rows divided-list">
                        {paginatedAvailableIps.map((ip) => (
                          <button
                            key={ip}
                            type="button"
                            onClick={() => handleChooseIp(ip)}
                            className="mgmt-row mgmt-row--interactive clientgroup-ip-row w-full text-left"
                          >
                            <span className="mgmt-row__title font-mono truncate">{ip}</span>
                            <Plus className="w-3.5 h-3.5 flex-shrink-0 text-themed-muted" />
                          </button>
                        ))}
                      </div>
                    </CustomScrollbar>
                  )}
                </div>
                {totalPages > 1 && (
                  <Pagination
                    currentPage={ipPage}
                    totalPages={totalPages}
                    totalItems={availableIps.length}
                    itemsPerPage={IPS_PER_PAGE}
                    onPageChange={setIpPage}
                    itemLabel={t('management.sections.clients.ipsLabel')}
                    showCard={false}
                    compact
                    className="mt-2"
                  />
                )}
              </>
            )
          )}
        </div>

        {/* Actions */}
        <div className="flex justify-end gap-3 pt-4 border-t border-themed-primary">
          <Button type="button" variant="filled" color="gray" onClick={onClose} disabled={saving}>
            {t('common.cancel')}
          </Button>
          <Button type="submit" loading={saving} disabled={saving || !nickname.trim()}>
            {isEditing
              ? t('modals.clientGroup.actions.saveChanges')
              : t('modals.clientGroup.actions.addNickname')}
          </Button>
        </div>
      </form>
    </Modal>
  );
};

export default ClientGroupModal;

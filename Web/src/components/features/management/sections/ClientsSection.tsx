import React, { useState, useMemo, useEffect, useLayoutEffect, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { AccordionSection } from '@components/ui/AccordionSection';
import { HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { AccordionGroupToggle } from '@components/ui/AccordionGroupToggle';
import { GroupHeading } from '@components/ui/GroupHeading';
import { TabPanel } from '@components/features/management/TabPanel';
import { RowActionsMenu } from '@components/ui/RowActionsMenu';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { ActionMenuItem, ActionMenuDangerItem } from '@components/ui/ActionMenu';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { Button } from '@components/ui/Button';
import Badge from '@components/ui/Badge';
import { SectionHeaderActions, SectionHeaderChip } from '@components/ui/SectionHeaderActions';
import { Tooltip } from '@components/ui/Tooltip';
import { Alert } from '@components/ui/Alert';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { Pagination } from '@components/ui/Pagination';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { Checkbox } from '@components/ui/Checkbox';
import { SettingRow } from '@components/ui/SettingRow';
import { ClientAddressChip } from '@components/ui/ClientAddressChip';
import { LoadingState, EmptyState } from '@components/ui/ManagerCard';
import { usePaginatedList } from '@hooks/usePaginatedList';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useSelectionSet } from '@hooks/useSelectionSet';
import { useTimeoutCallback } from '@hooks/useTimeoutCallback';
import { useClientGroups } from '@contexts/useClientGroups';
import { useClientHostnames } from '@contexts/useClientHostnames';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useStats, useDownloads } from '@contexts/DashboardDataContext/hooks';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import { resolveClientLabel } from '@utils/clientLabel';
import { getClientHostnameReasonKey } from '@utils/clientHostnameReason';
import { isValidIpAddress, parseIpCandidates } from '@utils/ipAddress';
import { useKnownClientIps } from '@/hooks/useKnownClientIps';
import { Users, EyeOff, Trash2, Edit2, ChevronDown, Network } from 'lucide-react';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import ClientGroupModal from '@components/modals/ClientGroupModal';
import type { SignalREventName } from '@contexts/SignalRContext/types';
import type { ClientGroup, ClientExclusionRule, ClientExclusionMode } from '../../../../types';
import '../managementSectionContent.css';
import './ClientsSection.css';

// Everything in the Client Hostnames panel is written by its one Save button, so the controls hold
// their own state until then and the button is the single place a save is reported.
interface HostnameForm {
  enabled: boolean;
  guestAccess: boolean;
  routerLookup: boolean;
  dockerLookup: boolean;
  resolver: string;
}

// The form field each switch writes, and the string that names it on screen.
const HOSTNAME_SWITCHES = [
  { key: 'enabled', label: 'toggle' },
  { key: 'guestAccess', label: 'guestAccess' },
  { key: 'routerLookup', label: 'routerLookup' },
  { key: 'dockerLookup', label: 'dockerLookup' }
] as const;

// Eight rows keeps the raw address list from out-weighing the nicknames it feeds.
const UNGROUPED_IPS_PER_PAGE = 8;

// A saved exclusion change raises this, and so does a log processing run, the end-of-activity edge
// and an eviction reset. The reload is one small request and it declines to run while the panel is
// being edited, so it answers the name rather than filtering on a reason.
const CLIENT_EXCLUSION_EVENTS: readonly SignalREventName[] = ['DownloadsRefresh'];

// The events above can land within a moment of each other, so the burst collapses into one load.
const REFRESH_DEBOUNCE_MS = 1000;

interface ClientsSectionProps {
  isAdmin: boolean;
  authMode: string;
  mockMode: boolean;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
}

const ClientsSection: React.FC<ClientsSectionProps> = ({
  isAdmin,
  mockMode,
  onError,
  onSuccess
}) => {
  const { t } = useTranslation();
  const { clientGroups, loading, error, deleteClientGroup, getGroupForIp } = useClientGroups();
  const {
    enabled: hostnamesEnabled,
    loading: hostnamesLoading,
    error: hostnamesError,
    setEnabled: setHostnamesEnabled,
    getHostnameForIp,
    reason: hostnamesReason,
    someUnnamedDismissed,
    dismissSomeUnnamed,
    settings: hostnameSettings,
    setSettings: setHostnameSettings
  } = useClientHostnames();
  const hostnamesReasonKey = getClientHostnameReasonKey(hostnamesReason);
  const visibleHostnamesReasonKey =
    hostnamesReason === 'someUnnamed' && someUnnamedDismissed ? null : hostnamesReasonKey;
  const { refreshStats } = useStats();
  const { refreshDownloads } = useDownloads();
  const { on, off, isConnected } = useSignalR();
  const scheduleReload = useTimeoutCallback(REFRESH_DEBOUNCE_MS);

  const [nicknamesExpanded, setNicknamesExpanded] = useState(false);
  useAccordionGroupItem('clients-nicknames', nicknamesExpanded, () =>
    setNicknamesExpanded((prev) => !prev)
  );
  const [exclusionsExpanded, setExclusionsExpanded] = useState(false);
  useAccordionGroupItem('clients-exclusions', exclusionsExpanded, () =>
    setExclusionsExpanded((prev) => !prev)
  );
  const [hostnamesExpanded, setHostnamesExpanded] = useState(false);
  useAccordionGroupItem('clients-hostnames', hostnamesExpanded, () =>
    setHostnamesExpanded((prev) => !prev)
  );
  const [savingHostnames, setSavingHostnames] = useState(false);
  const [hostnameForm, setHostnameForm] = useState<HostnameForm>({
    enabled: false,
    guestAccess: false,
    routerLookup: true,
    dockerLookup: true,
    resolver: ''
  });

  // ALL client IPs without time filtering - management sections should not be affected by time
  // filters - kept current as client groups change.
  const {
    clientIps: allClientIps,
    loading: loadingClients,
    refresh: refreshKnownClientIps
  } = useKnownClientIps();

  // A machine downloading for the first time joins this list without any client-group event to
  // announce it, and these two sections are where the list is browsed. Reloading as a section opens
  // is therefore the moment a new address can first be looked at, and it costs nothing while both
  // stay closed. Asking on every open is safe: the hook serves an already current list from what it
  // has and never restarts a load that is still running.
  const knownClientsVisible = nicknamesExpanded || exclusionsExpanded;
  useEffect(() => {
    if (!knownClientsVisible) return;
    refreshKnownClientIps();
  }, [knownClientsVisible, refreshKnownClientIps]);

  const [excludedRules, setExcludedRules] = useState<ClientExclusionRule[]>([]);
  const [savedExcludedRules, setSavedExcludedRules] = useState<ClientExclusionRule[]>([]);
  const [excludeInput, setExcludeInput] = useState('');
  const [selectedKnownIps, setSelectedKnownIps] = useState<string[]>([]);
  const [loadingExcluded, setLoadingExcluded] = useState(false);
  const [savingExcluded, setSavingExcluded] = useState(false);

  const serializeRules = useCallback(
    (rules: ClientExclusionRule[]) =>
      [...rules]
        .sort((a, b) => a.ip.localeCompare(b.ip))
        .map((rule) => `${rule.ip}:${rule.mode}`)
        .join('|'),
    []
  );

  const hasExcludedChanges = useMemo(
    () => serializeRules(excludedRules) !== serializeRules(savedExcludedRules),
    [excludedRules, savedExcludedRules, serializeRules]
  );

  // Read through a ref rather than a dependency so the check below sees what is on screen when the
  // reply lands, not what was on screen when the request went out.
  const hasExcludedChangesRef = useRef(hasExcludedChanges);
  hasExcludedChangesRef.current = hasExcludedChanges;

  const loadExcludedIps = useCallback(
    async (showLoading: boolean) => {
      if (!isAdmin) return;
      // Mock mode has no stored exclusion rules behind it, so the list stays as the state default
      // rather than showing a real machine's hidden clients.
      if (mockMode) return;
      if (showLoading) setLoadingExcluded(true);
      try {
        const response = await ApiService.getStatsExclusions();
        // A reply is a whole request old, and the user can have started editing inside that window.
        // What they typed is what they can see, so the server's copy waits for their next save.
        if (hasExcludedChangesRef.current) return;
        const rules = response.rules ?? [];
        setExcludedRules(rules);
        setSavedExcludedRules(rules);
      } catch (err) {
        onError(
          getErrorMessage(err) || t('management.sections.clients.errors.failedToLoadExcluded')
        );
      } finally {
        if (showLoading) setLoadingExcluded(false);
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [isAdmin, mockMode, onError]
  );

  useEffect(() => {
    loadExcludedIps(true);
  }, [loadExcludedIps]);

  // Another admin saving the list changes what this panel is showing, and the panel had no way of
  // hearing about it: it read its rules once, on mount, and the next save here wrote the old ones
  // back over the new. A draft is never replaced, and a background reload never raises the loading
  // flag, so it neither blanks the list nor disables the controls the user is holding.
  const reloadExclusions = useCallback(() => {
    if (hasExcludedChangesRef.current) return;
    void loadExcludedIps(false);
  }, [loadExcludedIps]);

  // Events raised while the socket was down are never delivered, so a genuine reconnect reloads.
  useReconnectRefetch(isConnected, reloadExclusions);

  useEffect(() => {
    const handleExclusionsChanged = () => {
      scheduleReload(reloadExclusions);
    };

    CLIENT_EXCLUSION_EVENTS.forEach((eventName) => on(eventName, handleExclusionsChanged));

    return () => {
      CLIENT_EXCLUSION_EVENTS.forEach((eventName) => off(eventName, handleExclusionsChanged));
    };
  }, [on, off, reloadExclusions, scheduleReload]);

  const invalidInputIps = useMemo(
    () => parseIpCandidates(excludeInput).filter((ip) => !isValidIpAddress(ip)),
    [excludeInput]
  );

  // New entries default to stats-only exclusion (visible, omitted from calculations).
  const DEFAULT_EXCLUSION_MODE: ClientExclusionMode = 'exclude';

  const addRules = useCallback((ips: string[]) => {
    setExcludedRules((prev) => {
      const existing = new Set(prev.map((rule) => rule.ip));
      const next = [...prev];
      for (const ip of ips) {
        if (!existing.has(ip)) {
          next.push({ ip, mode: DEFAULT_EXCLUSION_MODE });
          existing.add(ip);
        }
      }
      return next;
    });
  }, []);

  const handleAddExcluded = () => {
    const candidates = parseIpCandidates(excludeInput);
    const validCandidates = candidates.filter((ip) => isValidIpAddress(ip));

    if (validCandidates.length === 0) return;

    addRules(validCandidates);
    setExcludeInput('');
  };

  const handleAddKnownIps = () => {
    if (selectedKnownIps.length === 0) return;
    addRules(selectedKnownIps);
    setSelectedKnownIps([]);
  };

  const handleRemoveExcluded = (ip: string) => {
    setExcludedRules((prev) => prev.filter((rule) => rule.ip !== ip));
  };

  const handleChangeMode = (ip: string, mode: ClientExclusionMode) => {
    setExcludedRules((prev) => prev.map((rule) => (rule.ip === ip ? { ...rule, mode } : rule)));
  };

  const handleSaveExcluded = async () => {
    setSavingExcluded(true);
    try {
      const response = await ApiService.updateStatsExclusionRules(excludedRules);
      const rules = response.rules ?? [];
      setExcludedRules(rules);
      setSavedExcludedRules(rules);
      onSuccess(t('management.sections.clients.excludedIpsUpdated'));
      await refreshStats(true);
      await refreshDownloads();
    } catch (err) {
      onError(
        getErrorMessage(err) || t('management.sections.clients.errors.failedToUpdateExcluded')
      );
    } finally {
      setSavingExcluded(false);
    }
  };

  // An address that is already excluded stays on the list and says which exclusion it is under,
  // rather than vanishing from a control the reader is using to check what is excluded. It cannot
  // be picked again, because adding it a second time would do nothing.
  const knownClientOptions = useMemo(() => {
    return [...allClientIps]
      .sort((a, b) => a.localeCompare(b))
      .map((ip) => {
        const nickname = getGroupForIp(ip)?.nickname;
        const { text, substitutesAddress } = resolveClientLabel(ip, nickname, getHostnameForIp(ip));
        const excludedMode = excludedRules.find((rule) => rule.ip === ip)?.mode;
        return {
          value: ip,
          label: substitutesAddress ? `${text} (${ip})` : ip,
          description: excludedMode
            ? t(
                excludedMode === 'hide'
                  ? 'management.sections.clients.modeHide'
                  : 'management.sections.clients.modeExclude'
              )
            : undefined,
          disabled: excludedMode !== undefined
        };
      });
  }, [allClientIps, excludedRules, getGroupForIp, getHostnameForIp, t]);

  const [isModalOpen, setIsModalOpen] = useState(false);
  const [editingGroupId, setEditingGroupId] = useState<number | null>(null);
  const [quickNameIps, setQuickNameIps] = useState<string[]>([]);
  const [deletingGroupId, setDeletingGroupId] = useState<number | null>(null);
  const [deleteConfirmGroup, setDeleteConfirmGroup] = useState<ClientGroup | null>(null);
  const { selected: expandedGroups, toggle: toggleGroup } = useSelectionSet<string>();
  const [openMenuGroupId, setOpenMenuGroupId] = useState<number | null>(null);

  // Read back out of the list on every render instead of copied when Edit was clicked, so the
  // dialog works from what the last reload said and a save cannot be built on a nickname that has
  // moved on since. No id matches while creating, which is the null the dialog wants anyway.
  const editingGroup = useMemo(
    () => clientGroups.find((group) => group.id === editingGroupId) ?? null,
    [clientGroups, editingGroupId]
  );

  // The modal's member picker reads this same list, and the button that opens it for a new nickname
  // sits in the section header, which is there whether the section is open or shut - so opening the
  // modal can be the first time anyone looks at these addresses. Same non-forced request as the one
  // above: a list loaded moments ago is served as it stands, and a load already running is left to
  // finish rather than restarted under the open modal.
  useEffect(() => {
    if (!isModalOpen) return;
    refreshKnownClientIps();
  }, [isModalOpen, refreshKnownClientIps]);

  // Get all IPs that are in groups
  const groupedIps = useMemo(() => {
    const ips = new Set<string>();
    clientGroups.forEach((g) => g.memberIps.forEach((ip) => ips.add(ip)));
    return ips;
  }, [clientGroups]);

  // Get ungrouped clients (IPs that aren't in any group)
  const ungroupedClients = useMemo(() => {
    return allClientIps.filter((ip) => !groupedIps.has(ip));
  }, [allClientIps, groupedIps]);

  // The stats endpoint reports a combined nickname under one of its addresses, so the rest of that
  // nickname's members never appear in the known list. Folding the group members back in is what
  // lets the picker offer - and move - an address that already belongs to another nickname.
  const allKnownIps = useMemo(() => {
    const ips = new Set(allClientIps);
    clientGroups.forEach((group) => group.memberIps.forEach((ip) => ips.add(ip)));
    return [...ips].sort((a, b) => a.localeCompare(b));
  }, [allClientIps, clientGroups]);

  // Pagination for ungrouped clients
  const {
    page: ungroupedPage,
    setPage: setUngroupedPage,
    totalPages: totalUngroupedPages,
    paginatedItems: paginatedUngroupedClients
  } = usePaginatedList<string>({
    items: ungroupedClients,
    pageSize: UNGROUPED_IPS_PER_PAGE
  });

  // Keys are addresses rather than page indexes, so a selection survives paging.
  const unnamedSelection = useSelectionSet<string>();

  // An address that gains a nickname leaves this list; reading the selection back through the
  // current list keeps a stale key out of the count and out of the modal.
  const selectedUnnamedIps = useMemo(
    () => ungroupedClients.filter((ip) => unnamedSelection.isSelected(ip)),
    [ungroupedClients, unnamedSelection]
  );

  const allVisibleUnnamedSelected = unnamedSelection.allSelected(paginatedUngroupedClients);

  const handleSelectAllVisible = (): void => {
    unnamedSelection.setMany(paginatedUngroupedClients, !allVisibleUnnamedSelected);
  };

  const handleCreateGroup = () => {
    setEditingGroupId(null);
    setQuickNameIps([]);
    setIsModalOpen(true);
  };

  const handleNameSelected = () => {
    if (selectedUnnamedIps.length === 0) return;
    setEditingGroupId(null);
    setQuickNameIps(selectedUnnamedIps);
    setIsModalOpen(true);
  };

  const handleEditGroup = (group: ClientGroup) => {
    setEditingGroupId(group.id);
    setQuickNameIps([]);
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
      onSuccess(
        t('management.sections.clients.deletedNickname', { nickname: deleteConfirmGroup.nickname })
      );
      setDeleteConfirmGroup(null);
    } catch (err) {
      onError(getErrorMessage(err) || t('modals.clientGroup.errors.failedToDelete'));
    } finally {
      setDeletingGroupId(null);
    }
  };

  const handleModalClose = useCallback(() => {
    setIsModalOpen(false);
    setEditingGroupId(null);
    setQuickNameIps([]);
  }, []);

  // With the dialog fed by the live row, a nickname deleted from another tab leaves it holding
  // nothing, and it would quietly turn into the create form under whoever is typing in it. Close it
  // and say why, so the work that is about to be lost is not lost in silence. Before paint, or the
  // create form is what the user sees for a frame.
  useLayoutEffect(() => {
    if (!isModalOpen || editingGroupId === null) return;
    if (clientGroups.some((group) => group.id === editingGroupId)) return;
    handleModalClose();
    onError(t('management.sections.clients.errors.nicknameDeletedWhileEditing'));
  }, [isModalOpen, editingGroupId, clientGroups, handleModalClose, onError, t]);

  // The stored values arrive after the first render, so the form is seeded once they do and
  // follows any later change, including one made from another browser.
  useEffect(() => {
    setHostnameForm({
      enabled: hostnamesEnabled,
      guestAccess: hostnameSettings.guestAccess,
      routerLookup: hostnameSettings.routerLookup,
      dockerLookup: hostnameSettings.dockerLookup,
      resolver: hostnameSettings.resolver ?? ''
    });
  }, [hostnamesEnabled, hostnameSettings]);

  // Nothing is written until Save, so the panel needs to know whether anything is waiting.
  const hostnamesChanged =
    hostnameForm.enabled !== hostnamesEnabled ||
    hostnameForm.guestAccess !== hostnameSettings.guestAccess ||
    hostnameForm.routerLookup !== hostnameSettings.routerLookup ||
    hostnameForm.dockerLookup !== hostnameSettings.dockerLookup ||
    hostnameForm.resolver.trim() !== (hostnameSettings.resolver ?? '');

  // Written on demand rather than on every keystroke: the address is only usable once it is whole,
  // and each save clears every remembered name and re-asks the network.
  const handleHostnamesSave = useCallback(async () => {
    setSavingHostnames(true);
    try {
      // The servers to ask are written before the lookup is switched on, so the first queries the
      // toggle sets off already go where the admin just said they should.
      await setHostnameSettings({
        resolver: hostnameForm.resolver.trim(),
        guestAccess: hostnameForm.guestAccess,
        routerLookup: hostnameForm.routerLookup,
        dockerLookup: hostnameForm.dockerLookup
      });
      if (hostnameForm.enabled !== hostnamesEnabled) {
        await setHostnamesEnabled(hostnameForm.enabled);
      }
      onSuccess(t('management.sections.clients.hostnames.settingsSaved'));
    } catch (err: unknown) {
      onError(getErrorMessage(err));
    } finally {
      setSavingHostnames(false);
    }
  }, [
    setHostnameSettings,
    setHostnamesEnabled,
    hostnameForm,
    hostnamesEnabled,
    onSuccess,
    onError,
    t
  ]);

  const handleModalSuccess = (message: string) => {
    onSuccess(message);
    unnamedSelection.clear();
    handleModalClose();
  };

  const nicknamesHelpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.sections.clients.help.aboutTitle')}>
        {t('management.sections.clients.subtitle')}
      </HelpSection>
    </HelpPopover>
  );

  const exclusionsHelpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.sections.clients.help.exclusionsTitle')}>
        {t('management.sections.clients.exclusionsSummary')}
      </HelpSection>
    </HelpPopover>
  );

  const hostnamesHelpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.sections.clients.hostnames.help.aboutTitle')}>
        {t('management.sections.clients.hostnames.description')}
      </HelpSection>
    </HelpPopover>
  );

  return (
    <TabPanel tabId="clients">
      <div>
        <GroupHeading
          label={t('management.sections.clients.groupManagement')}
          actions={<AccordionGroupToggle />}
        />

        <div className="space-y-4">
          <AccordionSection
            title={t('management.sections.clients.title')}
            titleAccessory={nicknamesHelpAccessory}
            icon={Users}
            count={clientGroups.length}
            isExpanded={nicknamesExpanded}
            onToggle={() => setNicknamesExpanded((prev) => !prev)}
            badge={
              isAdmin ? (
                <SectionHeaderActions>
                  {/* The section's only primary action, so it stays outside a menu. A second
                      section action brings the kebab back and this button stays beside it. */}
                  <Button variant="filled" color="primary" size="md" onClick={handleCreateGroup}>
                    {t('management.sections.clients.addNickname')}
                  </Button>
                </SectionHeaderActions>
              ) : undefined
            }
          >
            <div className="space-y-4">
              {/* The context carries the technical message for the console; the reader gets a
                  translated one. */}
              {error && (
                <Alert color="red">
                  <span className="text-sm">
                    {t('management.sections.clients.errors.failedToLoadNicknames')}
                  </span>
                </Alert>
              )}
              {loading ? (
                <LoadingState
                  message={t('management.sections.clients.loadingNicknames')}
                  shape="list"
                  rows={3}
                />
              ) : clientGroups.length === 0 ? (
                <EmptyState
                  title={t('management.sections.clients.noNicknamesYet')}
                  subtitle={t('management.sections.clients.noNicknamesDesc')}
                />
              ) : (
                <div className="mgmt-list divided-list">
                  {clientGroups.map((group) => {
                    const isMultiIp = group.memberIps.length > 1;
                    const isExpanded = expandedGroups.has(String(group.id));
                    // A nickname can be left holding no addresses at all, and the meta line is
                    // the only place that would otherwise say so.
                    const ipSummary = isMultiIp
                      ? `${group.memberIps.length} ${t('management.sections.clients.ipsLabel')}`
                      : (group.memberIps[0] ?? t('management.sections.clients.noAddressesYet'));
                    const metaText = group.description
                      ? `${group.description} · ${ipSummary}`
                      : ipSummary;
                    return (
                      <div key={group.id}>
                        <div className="mgmt-row clients-nickname-row">
                          <div className="mgmt-row__body">
                            <div className="flex items-center gap-2 min-w-0">
                              <Tooltip content={group.nickname} className="flex min-w-0">
                                <p className="mgmt-row__title truncate">{group.nickname}</p>
                              </Tooltip>
                              {/* Combined is the default, so only separated groups are labelled. */}
                              {group.separateMemberRows && (
                                <Tooltip
                                  content={t('management.sections.clients.separateRowsTooltip')}
                                >
                                  <Badge variant="neutral" className="flex-shrink-0">
                                    {t('management.sections.clients.separateRowsLabel')}
                                  </Badge>
                                </Tooltip>
                              )}
                            </div>
                            <Tooltip content={metaText} className="block min-w-0">
                              <p className="mgmt-row__meta truncate">
                                {group.description && <span>{group.description} · </span>}
                                <span className="font-mono">{ipSummary}</span>
                              </p>
                            </Tooltip>
                          </div>
                          {/* Both controls share one square footprint and stay visible, so touch
                              and keyboard reach them without a hover first. */}
                          <div className="mgmt-row__actions">
                            {isAdmin && (
                              <RowActionsMenu
                                open={openMenuGroupId === group.id}
                                onOpenChange={(isOpen) =>
                                  setOpenMenuGroupId(isOpen ? group.id : null)
                                }
                              >
                                {(close) => (
                                  <>
                                    <ActionMenuItem
                                      icon={<Edit2 className="w-4 h-4" />}
                                      onClick={() => {
                                        close();
                                        handleEditGroup(group);
                                      }}
                                    >
                                      {t('common.edit')}
                                    </ActionMenuItem>
                                    <ActionMenuDangerItem
                                      icon={<Trash2 className="w-4 h-4" />}
                                      onClick={() => {
                                        close();
                                        handleDeleteGroup(group);
                                      }}
                                      disabled={deletingGroupId === group.id}
                                    >
                                      {t('common.delete')}
                                    </ActionMenuDangerItem>
                                  </>
                                )}
                              </RowActionsMenu>
                            )}
                            <Tooltip
                              content={
                                isExpanded
                                  ? t('management.sections.clients.collapseIps', {
                                      nickname: group.nickname
                                    })
                                  : t('management.sections.clients.expandIps', {
                                      nickname: group.nickname
                                    })
                              }
                            >
                              <Button
                                type="button"
                                variant="accordion"
                                size="sm"
                                open={isExpanded}
                                className="btn-icon-square btn-icon-square--sm pointer-target-44"
                                onClick={() => toggleGroup(String(group.id))}
                                aria-expanded={isExpanded}
                                aria-label={
                                  isExpanded
                                    ? t('management.sections.clients.collapseIps', {
                                        nickname: group.nickname
                                      })
                                    : t('management.sections.clients.expandIps', {
                                        nickname: group.nickname
                                      })
                                }
                              >
                                <ChevronDown
                                  className={`w-4 h-4 transition duration-200 ease-out${
                                    isExpanded
                                      ? ' rotate-180 text-themed-accent'
                                      : ' rotate-0 text-themed-muted'
                                  }`}
                                />
                              </Button>
                            </Tooltip>
                          </div>
                        </div>
                        {/* A glance at the membership. Editing it belongs in one place, and that
                            place is the nickname modal. */}
                        <CollapsibleRegion open={isExpanded} contentClassName="mgmt-row-detail">
                          {group.memberIps.length === 0 ? (
                            <EmptyState
                              variant="text"
                              title={t('management.sections.clients.noAddressesYet')}
                            />
                          ) : (
                            <div className="flex flex-wrap gap-2">
                              {group.memberIps.map((ip) => (
                                <ClientAddressChip
                                  key={ip}
                                  ip={ip}
                                  hostname={getHostnameForIp(ip)}
                                  state="readonly"
                                />
                              ))}
                            </div>
                          )}
                        </CollapsibleRegion>
                      </div>
                    );
                  })}
                </div>
              )}

              {/* Clients without nicknames. Kept on screen once any nickname exists, so "everything
                  is named" is stated rather than left as a section that quietly vanished. */}
              {(loadingClients || ungroupedClients.length > 0 || clientGroups.length > 0) && (
                <div className="space-y-2">
                  <div className="flex items-center gap-2">
                    <Tooltip
                      content={t('management.sections.clients.unnamedHint')}
                      position="bottom"
                    >
                      <h4 className="mgmt-subhead caps-label">
                        {t('management.sections.clients.withoutNicknames')}
                      </h4>
                    </Tooltip>
                    {!loadingClients && ungroupedClients.length > 0 && (
                      <Badge variant="neutral" className="badge-count">
                        {ungroupedClients.length}
                      </Badge>
                    )}
                  </div>

                  {loadingClients ? (
                    <LoadingState
                      message={t('management.sections.clients.loadingClients')}
                      shape="list"
                      rows={4}
                    />
                  ) : ungroupedClients.length === 0 ? (
                    <EmptyState
                      title={t('modals.clientGroup.emptyStates.everyAddressNamed')}
                      subtitle={t('modals.clientGroup.emptyStates.everyAddressNamedHint')}
                    />
                  ) : (
                    <>
                      {/* One naming action for the whole selection, instead of one button per
                          address repeated down the page. */}
                      {isAdmin && (
                        <div className="clients-unnamed-toolbar">
                          <Checkbox
                            checked={allVisibleUnnamedSelected}
                            onChange={handleSelectAllVisible}
                            label={t('management.sections.clients.selectAllVisible')}
                          />
                          <Badge variant="neutral" className="badge-count">
                            {t('management.sections.clients.selectedCount', {
                              count: selectedUnnamedIps.length
                            })}
                          </Badge>
                          <Button
                            variant="filled"
                            color="primary"
                            size="md"
                            onClick={handleNameSelected}
                            disabled={selectedUnnamedIps.length === 0}
                            className="clients-unnamed-toolbar__action"
                          >
                            {t('management.sections.clients.nameSelected')}
                          </Button>
                        </div>
                      )}
                      <div className="mgmt-list divided-list">
                        {paginatedUngroupedClients.map((ip) =>
                          isAdmin ? (
                            // The label makes the whole row the checkbox's hit area and gives the
                            // input its accessible name from the address it carries.
                            <label key={ip} className="mgmt-row clients-unnamed-row">
                              <Checkbox
                                checked={unnamedSelection.isSelected(ip)}
                                onChange={() => unnamedSelection.toggle(ip)}
                                className="flex-shrink-0"
                              />
                              <div className="mgmt-row__body">
                                <ClientIpDisplay
                                  clientIp={ip}
                                  className="mgmt-row__title truncate"
                                />
                              </div>
                            </label>
                          ) : (
                            <div key={ip} className="mgmt-row clients-unnamed-row">
                              <div className="mgmt-row__body">
                                <ClientIpDisplay
                                  clientIp={ip}
                                  className="mgmt-row__title truncate"
                                />
                              </div>
                            </div>
                          )
                        )}
                      </div>
                      {totalUngroupedPages > 1 && (
                        <Pagination
                          currentPage={ungroupedPage}
                          totalPages={totalUngroupedPages}
                          totalItems={ungroupedClients.length}
                          itemsPerPage={UNGROUPED_IPS_PER_PAGE}
                          onPageChange={setUngroupedPage}
                          itemLabel={t('management.sections.clients.ipsLabel')}
                          showCard={false}
                          compact
                        />
                      )}
                    </>
                  )}
                </div>
              )}
            </div>
          </AccordionSection>

          <AccordionSection
            title={t('management.sections.clients.excludeFromStats')}
            titleAccessory={exclusionsHelpAccessory}
            icon={EyeOff}
            count={excludedRules.length}
            isExpanded={exclusionsExpanded}
            onToggle={() => setExclusionsExpanded((prev) => !prev)}
          >
            {!isAdmin ? (
              <Alert color="yellow">
                <span className="text-sm">
                  {t('management.sections.clients.authenticateToManage')}
                </span>
              </Alert>
            ) : (
              <div className="space-y-4">
                <div className="space-y-3">
                  {/* What each mode does is the first thing anyone adding a client wants to know. */}
                  <Tooltip
                    content={t('management.sections.clients.exclusionsHint')}
                    position="bottom"
                  >
                    <h4 className="mgmt-subhead caps-label">
                      {t('management.sections.clients.pickFromKnownClients')}
                    </h4>
                  </Tooltip>
                  <div className="flex flex-col sm:flex-row sm:items-center gap-3">
                    <MultiSelectDropdown
                      options={knownClientOptions}
                      values={selectedKnownIps}
                      onChange={setSelectedKnownIps}
                      placeholder={t('management.sections.clients.selectClients')}
                      minSelections={0}
                      searchable
                      disabled={
                        loadingExcluded || savingExcluded || knownClientOptions.length === 0
                      }
                      className="w-full"
                    />
                    <Button
                      onClick={handleAddKnownIps}
                      variant="filled"
                      color="secondary"
                      className="sm:w-40"
                      disabled={selectedKnownIps.length === 0 || loadingExcluded || savingExcluded}
                    >
                      {t('management.sections.clients.addSelected')}
                    </Button>
                  </div>
                  {knownClientOptions.length === 0 && (
                    <EmptyState
                      variant="text"
                      title={t('management.sections.clients.noAddressesYet')}
                    />
                  )}

                  <div className="flex flex-col sm:flex-row sm:items-center gap-3">
                    <input
                      type="text"
                      value={excludeInput}
                      onChange={(e) => setExcludeInput(e.target.value)}
                      placeholder={t('management.sections.clients.addIpsPlaceholder')}
                      className="themed-input control-h-md w-full px-3 text-sm transition-colors"
                      disabled={loadingExcluded || savingExcluded}
                    />
                    <Button
                      onClick={handleAddExcluded}
                      variant="filled"
                      color="secondary"
                      className="sm:w-40"
                      disabled={
                        loadingExcluded ||
                        savingExcluded ||
                        excludeInput.trim().length === 0 ||
                        invalidInputIps.length > 0
                      }
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
                        {t('management.sections.clients.invalidIps', {
                          ips: invalidInputIps.join(', ')
                        })}
                      </span>
                    </Alert>
                  )}
                </div>

                {loadingExcluded ? (
                  <LoadingState
                    message={t('management.sections.clients.loadingExcludedIps')}
                    shape="list"
                    rows={3}
                  />
                ) : excludedRules.length === 0 ? (
                  <EmptyState
                    variant="text"
                    title={t('management.sections.clients.noExcludedIps')}
                  />
                ) : (
                  <div className="mgmt-list divided-list">
                    {excludedRules.map((rule) => (
                      <div key={rule.ip} className="mgmt-row clients-exclusion-row flex-wrap">
                        <div className="mgmt-row__body">
                          <div className="mgmt-row__title font-mono">
                            <ClientIpDisplay clientIp={rule.ip} showTooltip={false} />
                          </div>
                        </div>
                        <div className="mgmt-row__actions">
                          <SegmentedControl
                            size="sm"
                            showLabels
                            value={rule.mode}
                            onChange={(mode: string) =>
                              handleChangeMode(rule.ip, mode === 'hide' ? 'hide' : 'exclude')
                            }
                            options={[
                              {
                                value: 'exclude',
                                label: t('management.sections.clients.modeExclude'),
                                tooltip: t('management.sections.clients.modeExcludeTooltip'),
                                disabled: savingExcluded
                              },
                              {
                                value: 'hide',
                                label: t('management.sections.clients.modeHide'),
                                tooltip: t('management.sections.clients.modeHideTooltip'),
                                disabled: savingExcluded
                              }
                            ]}
                          />
                          <Tooltip content={t('management.sections.clients.removeIp')}>
                            <Button
                              variant="filled"
                              color="secondary"
                              size="sm"
                              className="btn-icon-square btn-icon-square--sm delete-hover pointer-target-44"
                              onClick={() => handleRemoveExcluded(rule.ip)}
                              disabled={savingExcluded}
                              aria-label={t('management.sections.clients.removeIp')}
                            >
                              <Trash2 className="w-4 h-4" />
                            </Button>
                          </Tooltip>
                        </div>
                      </div>
                    ))}
                  </div>
                )}

                <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 pt-1">
                  <span className="text-xs text-themed-muted">
                    {hasExcludedChanges ? t('management.sections.clients.unsavedChanges') : ''}
                  </span>
                  {/* Adding a rule only stages it; this is the button that persists the card. */}
                  <Button
                    onClick={handleSaveExcluded}
                    variant="filled"
                    color="primary"
                    disabled={!hasExcludedChanges || savingExcluded || loadingExcluded}
                    loading={savingExcluded}
                    className="sm:w-40"
                  >
                    {t('management.sections.clients.saveChanges')}
                  </Button>
                </div>
              </div>
            )}
          </AccordionSection>

          <AccordionSection
            title={t('management.sections.clients.hostnames.title')}
            titleAccessory={hostnamesHelpAccessory}
            icon={Network}
            isExpanded={hostnamesExpanded}
            onToggle={() => setHostnamesExpanded((prev) => !prev)}
            badge={
              <SectionHeaderChip variant={hostnamesEnabled ? 'success' : 'neutral'}>
                {t(
                  hostnamesEnabled
                    ? 'management.sections.clients.hostnames.enabled'
                    : 'management.sections.clients.hostnames.disabled'
                )}
              </SectionHeaderChip>
            }
          >
            {!isAdmin ? (
              <Alert color="yellow">
                <span className="text-sm">
                  {t('management.sections.clients.authenticateToManage')}
                </span>
              </Alert>
            ) : (
              <div className="space-y-3">
                {hostnamesError ? (
                  <Alert color="red">{hostnamesError}</Alert>
                ) : visibleHostnamesReasonKey ? (
                  <Alert
                    color="yellow"
                    withCloseButton={hostnamesReason === 'someUnnamed'}
                    onClose={hostnamesReason === 'someUnnamed' ? dismissSomeUnnamed : undefined}
                    closeButtonLabel={t('common.dismiss')}
                  >
                    {t(visibleHostnamesReasonKey)}
                  </Alert>
                ) : null}
                {HOSTNAME_SWITCHES.map(({ key, label }) => (
                  <SettingRow
                    key={key}
                    checked={hostnameForm[key]}
                    onChange={(checked) =>
                      setHostnameForm((previous) => ({ ...previous, [key]: checked }))
                    }
                    label={t(`management.sections.clients.hostnames.${label}Label`)}
                    description={t(`management.sections.clients.hostnames.${label}Description`)}
                    disabled={savingHostnames}
                  />
                ))}
                <div className="space-y-2 py-2">
                  <p className="text-sm font-medium text-themed-primary">
                    {t('management.sections.clients.hostnames.resolverLabel')}
                  </p>
                  <p className="text-xs text-themed-muted">
                    {t('management.sections.clients.hostnames.resolverDescription')}
                  </p>
                  <input
                    type="text"
                    value={hostnameForm.resolver}
                    onChange={(e) =>
                      setHostnameForm((previous) => ({ ...previous, resolver: e.target.value }))
                    }
                    placeholder={t('management.sections.clients.hostnames.resolverPlaceholder')}
                    className="themed-input control-h-md w-full px-3 text-sm transition-colors"
                    disabled={savingHostnames}
                    aria-label={t('management.sections.clients.hostnames.resolverLabel')}
                  />
                </div>

                {/* The switches and the resolver are staged until this button, exactly like the
                    exclusions panel, so both panels say the same thing the same way. */}
                <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 pt-1">
                  <span className="text-xs text-themed-muted">
                    {hostnamesChanged ? t('management.sections.clients.unsavedChanges') : ''}
                  </span>
                  <Button
                    onClick={() => void handleHostnamesSave()}
                    variant="filled"
                    color="primary"
                    className="sm:w-40"
                    disabled={savingHostnames || hostnamesLoading || !hostnamesChanged}
                    loading={savingHostnames}
                  >
                    {t('management.sections.clients.saveChanges')}
                  </Button>
                </div>
              </div>
            )}
          </AccordionSection>
        </div>
      </div>

      {/* Edit/Create Modal */}
      <ClientGroupModal
        isOpen={isModalOpen}
        onClose={handleModalClose}
        group={editingGroup}
        knownIps={allKnownIps}
        initialIps={quickNameIps}
        onSuccess={handleModalSuccess}
        onError={onError}
      />

      {/* Delete Confirmation Modal */}
      <ConfirmationModal
        opened={deleteConfirmGroup !== null}
        onClose={() => setDeleteConfirmGroup(null)}
        onConfirm={confirmDeleteGroup}
        title={t('management.sections.clients.deleteNickname')}
        confirmLabel={t('management.sections.clients.delete')}
        loading={deletingGroupId !== null}
      >
        <p className="text-themed-secondary">
          {t('management.sections.clients.deleteNicknameConfirm', {
            nickname: deleteConfirmGroup?.nickname
          })}
        </p>

        <Alert color="yellow">
          <p className="text-sm">{t('management.sections.clients.deleteNicknameWarning')}</p>
        </Alert>
      </ConfirmationModal>
    </TabPanel>
  );
};

export default ClientsSection;

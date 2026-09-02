import React, { useState, useCallback, useEffect, useMemo, useRef } from 'react';
import { Plus, Users } from 'lucide-react';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import Badge from '@components/ui/Badge';
import { Checkbox } from '@components/ui/Checkbox';
import FormField from '@components/ui/FormField';
import { ClientAddressChip } from '@components/ui/ClientAddressChip';
import { SearchInput } from '@components/ui/SearchInput';
import { Tooltip } from '@components/ui/Tooltip';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { EmptyState, LoadingState } from '@components/ui/ManagerCard';
import { useClientGroups } from '@contexts/useClientGroups';
import { useClientHostnames } from '@contexts/useClientHostnames';
import { useSelectionSet } from '@hooks/useSelectionSet';
import { useTimeoutCallback } from '@hooks/useTimeoutCallback';
import { useTranslation } from 'react-i18next';
import ApiService, { type ClientAddressLookupReason } from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import { resolveClientLabel } from '@utils/clientLabel';
import { isPlausibleHostname, isValidIpAddress, parseIpCandidates } from '@utils/ipAddress';
import type { ClientGroup } from '../../types';
import '@components/features/management/managementSectionContent.css';
import './ClientGroupModal.css';

// The row mode is a boolean on the wire; these are the segmented control's option ids.
const ROW_MODE_COMBINED = 'combined';
const ROW_MODE_SEPARATE = 'separate';

// Long enough that a full address typed at speed filters once, short enough that the
// list still feels like it reacts to the keystroke.
const SEARCH_DEBOUNCE_MS = 250;

// 18rem = 288px = 8 rows at the 36px pick-row height. The picker scrolls and nothing
// else, so this is the only thing bounding it.
const PICKER_MAX_HEIGHT = '18rem';

/** One i18n key per reason a name lookup came back with nothing, or null when nothing is to be
 *  said. A Record keyed by every member of the union forces this to stay exhaustive. */
const lookupReasonKeys: Readonly<Record<ClientAddressLookupReason, string | null>> = {
  none: null,
  noRecords: 'modals.clientGroup.lookup.noRecords',
  noResolver: 'modals.clientGroup.lookup.noResolver',
  resolverTimeout: 'modals.clientGroup.lookup.resolverTimeout'
};

/**
 * What the name lookup last did. One value rather than a flag beside a message, so the button, the
 * line under it and the addresses it chose can never disagree about which name is being talked
 * about.
 */
type HostnameLookup =
  | { status: 'idle' }
  | { status: 'looking'; hostname: string }
  | { status: 'resolved'; hostname: string; added: string[]; ownedElsewhere: string[] }
  | { status: 'empty'; hostname: string; reason: ClientAddressLookupReason }
  | { status: 'failed'; hostname: string };

/** One address offered by the picker, with the nickname that already holds it. */
interface PickerRow {
  address: string;
  /** Set when a DIFFERENT nickname owns the address, which makes the row unpickable. */
  ownerNickname: string | null;
  /**
   * True for an address the install has no record of. It is offered because it was typed into the
   * box, not because anything has ever downloaded from it, so the row says so rather than sitting
   * among the seen addresses looking identical to them.
   */
  unseen: boolean;
}

/**
 * Every address this nickname could still take, narrowed by the search text. Written as a plain
 * function so a keystroke can resolve the list for search text the debounce has not applied yet.
 */
const matchAddresses = (
  knownIps: string[],
  currentMemberSet: Set<string>,
  search: string,
  ownerOfIp: (ip: string) => ClientGroup | null,
  savedGroupId: number | null
): PickerRow[] => {
  const needle = search.trim().toLowerCase();
  const toRow = (ip: string, unseen: boolean): PickerRow => {
    const owner = ownerOfIp(ip);
    const ownedElsewhere = owner !== null && owner.id !== savedGroupId;
    return { address: ip, ownerNickname: ownedElsewhere ? owner.nickname : null, unseen };
  };

  const knownIpSet = new Set(knownIps);
  // A whole address typed into the box asks for that address; it does not filter the ones already
  // listed. So it is offered first and offered even though nothing has been seen from it, which is
  // what lets a machine be named before it has ever downloaded anything. A pasted list works the
  // same way, so a set of machines that are all still quiet gets named in one go.
  const typed = parseIpCandidates(search)
    .filter((ip) => isValidIpAddress(ip) && !knownIpSet.has(ip) && !currentMemberSet.has(ip))
    .filter((ip, index, all) => all.indexOf(ip) === index)
    .map((ip) => toRow(ip, true));

  const matched = knownIps
    .filter((ip) => !currentMemberSet.has(ip))
    .filter((ip) => needle === '' || ip.toLowerCase().includes(needle))
    .map((ip) => toRow(ip, false));

  return [...typed, ...matched];
};

/** The nearest row at or past `from` in the `step` direction that this nickname may take. */
const enabledIndexIn = (rows: PickerRow[], from: number, step: number): number => {
  for (let i = from; i >= 0 && i < rows.length; i += step) {
    if (rows[i].ownerNickname === null) return i;
  }
  return -1;
};

interface ClientGroupModalProps {
  isOpen: boolean;
  onClose: () => void;
  group: ClientGroup | null; // null for create, ClientGroup for edit
  /**
   * Every address the install knows about, grouped and ungrouped alike, so an address
   * another nickname holds can be shown with its owner instead of silently missing.
   */
  knownIps: string[];
  /** Create mode only: IPs pre-selected when the modal opens (quick-name flow). */
  initialIps?: string[];
  onSuccess: (message: string) => void;
  onError?: (message: string) => void; // Optional, errors shown inline in modal
}

const ClientGroupModal: React.FC<ClientGroupModalProps> = ({
  isOpen,
  onClose,
  group,
  knownIps,
  initialIps,
  onSuccess
}) => {
  const { t } = useTranslation();
  const {
    createClientGroup,
    updateClientGroup,
    setMembers,
    refreshGroups,
    getGroupForIp,
    loading: groupsLoading,
    error: groupsError
  } = useClientGroups();
  const { getHostnameForIp } = useClientHostnames();
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [rejectedIps, setRejectedIps] = useState<string[]>([]);

  // Form state
  const [nickname, setNickname] = useState('');
  const [description, setDescription] = useState('');
  const [separateMemberRows, setSeparateMemberRows] = useState(false);
  /** Edit mode: current members marked for removal, still shown so the mark is undoable. */
  const [removedIps, setRemovedIps] = useState<string[]>([]);
  /**
   * Set once a create request has succeeded, so a second Save after a partial result
   * edits the nickname that now exists instead of creating a duplicate.
   */
  const [createdGroupId, setCreatedGroupId] = useState<number | null>(null);

  // Picker state. The input value is immediate; only the filtering waits for the debounce.
  const [searchInput, setSearchInput] = useState('');
  const [searchQuery, setSearchQuery] = useState('');
  // Held by address for the same reason the range anchor below is: choosing a row takes it out
  // of the list, so an index kept from the click names whichever row slid into that slot and
  // leaves the cursor highlight sitting on an address the user never touched.
  const [activeAddress, setActiveAddress] = useState<string | null>(null);
  const [lastTouchedAddress, setLastTouchedAddress] = useState<string | null>(null);

  const [lookup, setLookup] = useState<HostnameLookup>({ status: 'idle' });
  /**
   * The name each address was found under, so a chip for a machine that has never downloaded
   * anything reads as the machine rather than as a number nobody recognises. Kept here because a
   * forward lookup is the only thing that knows it: the reverse-name map is built from addresses
   * the install has already seen.
   */
  const [lookupNames, setLookupNames] = useState<Record<string, string>>({});

  const chosen = useSelectionSet<string>();
  const clearChosen = chosen.clear;
  const setChosenMany = chosen.setMany;
  const toggleChosen = chosen.toggle;

  const scheduleSearch = useTimeoutCallback(SEARCH_DEBOUNCE_MS);
  const searchRef = useRef<HTMLInputElement>(null);
  const rowRefs = useRef<Map<string, HTMLButtonElement>>(new Map());
  /** Row index to focus once the list has re-rendered without the rows just chosen. */
  const pendingFocusRef = useRef<number | null>(null);
  /** Which nickname the form fields were last seeded for. */
  const seededForRef = useRef<string | null>(null);
  /**
   * Bumped for every editing session and every lookup. An answer that comes back after the dialog
   * was closed and reopened belongs to a question nobody is looking at any more, and applying it
   * would choose addresses in a nickname that never asked for them.
   */
  const lookupTokenRef = useRef(0);
  /**
   * The stamp of the copy this editing session is working from. Taken once when the session starts,
   * then moved forward by this session's own writes so a second Save is not turned down by the
   * first one, and by a refusal so the retry is checked against what the server now holds.
   */
  const expectedUpdatedAtRef = useRef<string | null>(null);
  /** Holds focus while a save disables the control the editor was on. */
  const formRef = useRef<HTMLFormElement>(null);

  const isEditing = group !== null;
  const savedGroupId = group !== null ? group.id : createdGroupId;

  // Seed the form once per editing session. A background refresh hands down a new group
  // object for the same nickname, and re-seeding on that would wipe an in-progress edit.
  useEffect(() => {
    if (!isOpen) {
      seededForRef.current = null;
      lookupTokenRef.current += 1;
      return;
    }
    const sessionKey = group ? `group-${group.id}` : 'new';
    if (seededForRef.current === sessionKey) return;
    seededForRef.current = sessionKey;
    lookupTokenRef.current += 1;
    expectedUpdatedAtRef.current = group?.updatedAtUtc ?? null;

    if (group) {
      setNickname(group.nickname);
      setDescription(group.description || '');
      setSeparateMemberRows(group.separateMemberRows);
    } else {
      setNickname('');
      setDescription('');
      setSeparateMemberRows(false);
    }
    setRemovedIps([]);
    setCreatedGroupId(null);
    clearChosen();
    if (!group && initialIps && initialIps.length > 0) {
      setChosenMany(initialIps, true);
    }
    setError(null);
    setRejectedIps([]);
    setSearchInput('');
    setSearchQuery('');
    // Cancels a debounce left pending by the previous session.
    scheduleSearch(() => setSearchQuery(''));
    setActiveAddress(null);
    setLastTouchedAddress(null);
    setLookup({ status: 'idle' });
    setLookupNames({});
  }, [isOpen, group, initialIps, clearChosen, setChosenMany, scheduleSearch]);

  const currentMemberIps = useMemo(() => group?.memberIps ?? [], [group]);
  const currentMemberSet = useMemo(() => new Set(currentMemberIps), [currentMemberIps]);
  const removedSet = useMemo(() => new Set(removedIps), [removedIps]);
  const knownIpSet = useMemo(() => new Set(knownIps), [knownIps]);

  // Chosen addresses in the order the picker offers them, so the chip row is stable.
  const chosenList = useMemo(() => {
    const offered = knownIps.filter((ip) => chosen.selected.has(ip));
    const unlisted = [...chosen.selected].filter((ip) => !knownIpSet.has(ip));
    return [...offered, ...unlisted];
  }, [knownIps, knownIpSet, chosen.selected]);

  /** The membership Save would write: current members minus the marked ones, plus the chosen. */
  const pendingMemberIps = useMemo(
    () => [...currentMemberIps.filter((ip) => !removedSet.has(ip)), ...chosenList],
    [currentMemberIps, removedSet, chosenList]
  );

  // Everything this nickname could still take: every known address it does not already hold.
  const addressableCount = useMemo(
    () => knownIps.filter((ip) => !currentMemberSet.has(ip)).length,
    [knownIps, currentMemberSet]
  );

  const matchingRows = useMemo<PickerRow[]>(
    () => matchAddresses(knownIps, currentMemberSet, searchQuery, getGroupForIp, savedGroupId),
    [knownIps, currentMemberSet, searchQuery, getGroupForIp, savedGroupId]
  );

  // Select-all covers every match a nickname could take, chosen or not, so the checkbox
  // reads as "all of them are in" even though a chosen row leaves the list.
  const selectableAddresses = useMemo(
    () => matchingRows.filter((row) => row.ownerNickname === null).map((row) => row.address),
    [matchingRows]
  );

  // "x of y match" counts the addresses the install knows about against the addresses it knows
  // about. A row that exists only because it was typed in is in neither total, and counting it
  // would put the shown figure above the one it is shown out of.
  const seenMatchCount = useMemo(
    () => matchingRows.filter((row) => !row.unseen).length,
    [matchingRows]
  );

  const pickerRows = useMemo(
    () => matchingRows.filter((row) => !chosen.selected.has(row.address)),
    [matchingRows, chosen.selected]
  );

  const allMatchingChosen = chosen.allSelected(selectableAddresses);

  const firstEnabledIndex = useMemo(() => enabledIndexIn(pickerRows, 0, 1), [pickerRows]);

  // Where each offered address sits in `matchingRows`. Ranges are resolved and sliced in those
  // coordinates because `matchingRows` keeps the rows already chosen, while `pickerRows` drops a
  // row the moment it is taken.
  const matchingIndexByAddress = useMemo(() => {
    const positions = new Map<string, number>();
    matchingRows.forEach((row, index) => positions.set(row.address, index));
    return positions;
  }, [matchingRows]);

  const matchingIndexOf = useCallback(
    (address: string | null): number => {
      if (address === null) return -1;
      return matchingIndexByAddress.get(address) ?? -1;
    },
    [matchingIndexByAddress]
  );

  // The range anchor is held by address, not by position: choosing a row takes it out of the
  // list, so an index kept from an earlier keystroke points at whichever row slid into that slot
  // and extends the range over addresses the user never touched.
  // Looking it up in `pickerRows` removed it in the same commit that set it, which left the anchor
  // permanently unresolved and every range silently reduced to a single toggle.
  const anchorIndex = matchingIndexOf(lastTouchedAddress);
  // Exactly one row is reachable by Tab; the arrows move it from there. When the list
  // shrinks under the cursor and leaves it on an address another nickname owns, the
  // fallback keeps a reachable row rather than dropping the list out of the tab order.
  // Where the cursor sits now. An address that has just been chosen is gone from the list, so
  // this falls to -1 and no row is left wearing the cursor.
  const activeIndex = useMemo(
    () =>
      activeAddress === null ? -1 : pickerRows.findIndex((row) => row.address === activeAddress),
    [activeAddress, pickerRows]
  );

  const rovingIndex =
    activeIndex >= 0 && pickerRows[activeIndex]?.ownerNickname === null
      ? activeIndex
      : firstEnabledIndex;

  const focusRow = useCallback((address: string | undefined): void => {
    if (address === undefined) return;
    const node = rowRefs.current.get(address);
    if (!node) return;
    node.focus();
    node.scrollIntoView({ block: 'nearest' });
  }, []);

  // A chosen row leaves the list, so the keyboard cursor stays put by index and the row
  // that slid into that position takes focus.
  useEffect(() => {
    const index = pendingFocusRef.current;
    if (index === null) {
      // A row chosen with the mouse is unmounted while it holds focus, which leaves focus on the
      // document body. The dialog only traps Tab while focus is on one of its descendants, so the
      // search box takes it back rather than letting the next Tab walk the page behind the modal.
      if (document.activeElement === document.body) {
        searchRef.current?.focus();
      }
      return;
    }
    pendingFocusRef.current = null;
    const target = Math.min(index, pickerRows.length - 1);
    if (target < 0) {
      searchRef.current?.focus();
      return;
    }
    focusRow(pickerRows[target].address);
  }, [pickerRows, focusRow]);

  // Saving disables the search box and the submit button, and a browser blurs a control it
  // disables. The dialog only traps Tab while focus is on one of its descendants, so focus left on
  // the document body would let the next Tab walk the page behind the modal.
  useEffect(() => {
    if (!saving) return;
    const focused = document.activeElement;
    if (focused !== null && focused !== document.body) return;
    formRef.current?.focus();
  }, [saving]);

  const nextEnabledIndex = useCallback(
    (from: number, step: number): number => enabledIndexIn(pickerRows, from, step),
    [pickerRows]
  );

  const moveActive = useCallback(
    (target: number): void => {
      if (target < 0) return;
      setActiveAddress(pickerRows[target].address);
      focusRow(pickerRows[target].address);
    },
    [pickerRows, focusRow]
  );

  const applySearch = useCallback(
    (value: string): void => {
      setSearchInput(value);
      scheduleSearch(() => setSearchQuery(value));
    },
    [scheduleSearch]
  );

  const handleSearchChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>): void => {
      applySearch(e.target.value);
    },
    [applySearch]
  );

  const handleClearSearch = useCallback((): void => {
    applySearch('');
    // Clearing is an explicit action, so the filter drops now instead of after the debounce.
    setSearchQuery('');
    setActiveAddress(null);
    setLastTouchedAddress(null);
    searchRef.current?.focus();
  }, [applySearch]);

  const isPickable = useCallback(
    (index: number): boolean => pickerRows[index]?.ownerNickname === null,
    [pickerRows]
  );

  const chooseAddress = useCallback(
    (address: string): void => {
      toggleChosen(address);
      setLastTouchedAddress(address);
    },
    [toggleChosen]
  );

  const handleToggleAddress = useCallback(
    (index: number): void => {
      if (!isPickable(index)) return;
      chooseAddress(pickerRows[index].address);
    },
    [pickerRows, isPickable, chooseAddress]
  );

  /** Both ends are `matchingRows` positions. Rows already chosen stay in the span and re-taking
   *  one changes nothing, so a range reads the same whether it is drawn up or down the list. */
  const handleSelectRange = useCallback(
    (fromIndex: number, toIndex: number): void => {
      // A negative end would be read by slice as an offset from the tail of the list and quietly
      // take rows nowhere near the ones the user drew across.
      if (fromIndex < 0 || toIndex < 0) return;
      const start = Math.min(fromIndex, toIndex);
      const end = Math.max(fromIndex, toIndex);
      const addresses = matchingRows
        .slice(start, end + 1)
        .filter((row) => row.ownerNickname === null)
        .map((row) => row.address);
      if (addresses.length > 0) {
        setChosenMany(addresses, true);
      }
      setLastTouchedAddress(matchingRows[toIndex]?.address ?? null);
    },
    [matchingRows, setChosenMany]
  );

  const handleToggleRemoval = useCallback((ip: string): void => {
    setRemovedIps((prev) =>
      prev.includes(ip) ? prev.filter((item) => item !== ip) : [...prev, ip]
    );
  }, []);

  const handleSelectAllToggle = useCallback((): void => {
    setChosenMany(selectableAddresses, !allMatchingChosen);
  }, [setChosenMany, selectableAddresses, allMatchingChosen]);

  /** The name in the box worth asking the resolver about, or null when the text is not one. */
  const lookupCandidate = useMemo((): string | null => {
    const typed = searchInput.trim().replace(/\.$/, '');
    return isPlausibleHostname(typed) ? typed : null;
  }, [searchInput]);

  /**
   * Asks the network what a name resolves to and takes the addresses it gives back. This is the
   * other direction from the reverse names shown beside client addresses, and the one that works
   * on a LAN whose DNS answers forward queries but publishes no reverse zone.
   */
  const handleLookupHostname = useCallback(async (): Promise<void> => {
    if (lookupCandidate === null) return;
    const hostname = lookupCandidate;
    const token = lookupTokenRef.current + 1;
    lookupTokenRef.current = token;
    setLookup({ status: 'looking', hostname });

    try {
      const result = await ApiService.resolveClientAddresses(hostname);
      if (lookupTokenRef.current !== token) return;

      if (result.addresses.length === 0) {
        setLookup({ status: 'empty', hostname, reason: result.reason });
        return;
      }

      const added: string[] = [];
      const ownedElsewhere: string[] = [];
      for (const address of result.addresses) {
        const owner = getGroupForIp(address);
        if (owner !== null && owner.id !== savedGroupId) {
          ownedElsewhere.push(address);
          continue;
        }
        // An address this nickname already holds is in already; taking it again would list it
        // twice in the chips and count it twice in the badge above them.
        if (currentMemberSet.has(address)) continue;
        added.push(address);
      }

      if (added.length > 0) {
        setChosenMany(added, true);
        setLookupNames((prev) => {
          const next = { ...prev };
          for (const address of added) {
            next[address] = hostname;
          }
          return next;
        });
        // The name has done its job. Leaving it in the box would hold the picker on a filter that
        // matches no address at all, which reads as an empty list rather than a finished lookup.
        handleClearSearch();
      }
      setLookup({ status: 'resolved', hostname, added, ownedElsewhere });
    } catch (err) {
      if (lookupTokenRef.current !== token) return;
      console.error('Failed to look up client hostname:', getErrorMessage(err));
      setLookup({ status: 'failed', hostname });
    }
  }, [
    lookupCandidate,
    getGroupForIp,
    savedGroupId,
    currentMemberSet,
    setChosenMany,
    handleClearSearch
  ]);

  const handleRowClick = useCallback(
    (index: number, e: React.MouseEvent<HTMLButtonElement>): void => {
      // A row that changes nothing must not arm the pending focus, or the next
      // unrelated list change would pull focus off whatever the user moved on to.
      if (!isPickable(index)) return;
      // Only a keyboard activation puts the cursor back in the list. Enter and Space on a button
      // arrive here as a click with no click count, while a real press carries one. Moving focus
      // after a mouse click lands it on whichever row slid into the vacated slot, which then wears
      // the cursor surface, and that surface outranks the hover one: the list reads as stuck on a
      // row nobody pointed at, and pointing at any other row appears to do nothing.
      if (e.detail === 0) {
        pendingFocusRef.current = index;
      }
      // An anchor the search text has since ruled out cannot name a span the user can see, so
      // the click stays a plain toggle rather than reaching across rows that are not listed.
      if (e.shiftKey && anchorIndex >= 0) {
        handleSelectRange(anchorIndex, matchingIndexOf(pickerRows[index].address));
        return;
      }
      handleToggleAddress(index);
    },
    [isPickable, anchorIndex, handleSelectRange, handleToggleAddress, matchingIndexOf, pickerRows]
  );

  const handleRowKeyDown = useCallback(
    (index: number, e: React.KeyboardEvent<HTMLButtonElement>): void => {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        const step = e.key === 'ArrowDown' ? 1 : -1;
        const target = nextEnabledIndex(index + step, step);
        if (target < 0) return;
        if (e.shiftKey) {
          pendingFocusRef.current = index;
          const from = anchorIndex >= 0 ? anchorIndex : matchingIndexOf(pickerRows[index].address);
          handleSelectRange(from, matchingIndexOf(pickerRows[target].address));
          return;
        }
        moveActive(target);
        return;
      }
      if (e.key === 'Enter') {
        // Type, Enter, type, Enter: the address is taken and the caret is back in the search box.
        e.preventDefault();
        if (!isPickable(index)) return;
        handleToggleAddress(index);
        searchRef.current?.focus();
        return;
      }
      if (e.key === ' ') {
        e.preventDefault();
        if (!isPickable(index)) return;
        pendingFocusRef.current = index;
        handleToggleAddress(index);
        return;
      }
      if (e.key === 'Home' || e.key === 'End') {
        e.preventDefault();
        moveActive(
          e.key === 'Home' ? nextEnabledIndex(0, 1) : nextEnabledIndex(pickerRows.length - 1, -1)
        );
        return;
      }
      if (e.key === 'Escape' && searchInput !== '') {
        // Only a filled search box swallows Escape; an empty one lets the modal close.
        e.stopPropagation();
        handleClearSearch();
      }
    },
    [
      nextEnabledIndex,
      handleSelectRange,
      handleToggleAddress,
      isPickable,
      moveActive,
      pickerRows,
      matchingIndexOf,
      anchorIndex,
      searchInput,
      handleClearSearch
    ]
  );

  /**
   * The debounce holds the filter back, so a key pressed inside that window would act on rows the
   * search text has already ruled out: Enter would take an address the user is not looking at, and
   * an arrow would focus a row that unmounts a moment later, dropping focus out of the dialog.
   * Applying the typed text here settles the list first. Returns null when nothing was pending.
   */
  const flushSearch = useCallback((): PickerRow[] | null => {
    if (searchInput === searchQuery) return null;
    setSearchQuery(searchInput);
    // The keyboard cursor referred to a row the new text may not offer at all.
    setActiveAddress(null);
    return matchAddresses(
      knownIps,
      currentMemberSet,
      searchInput,
      getGroupForIp,
      savedGroupId
    ).filter((row) => !chosen.selected.has(row.address));
  }, [
    searchInput,
    searchQuery,
    knownIps,
    currentMemberSet,
    getGroupForIp,
    savedGroupId,
    chosen.selected
  ]);

  const handleSearchKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>): void => {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        const step = e.key === 'ArrowDown' ? 1 : -1;
        const flushed = flushSearch();
        if (flushed !== null) {
          // Those rows have not rendered yet, so focus is handed to the effect that runs once
          // they have.
          pendingFocusRef.current = enabledIndexIn(
            flushed,
            step === 1 ? 0 : flushed.length - 1,
            step
          );
          return;
        }
        moveActive(nextEnabledIndex(step === 1 ? 0 : pickerRows.length - 1, step));
        return;
      }
      if (e.key === 'Enter') {
        // A search box must never submit the form; it takes the active address instead.
        e.preventDefault();
        const flushed = flushSearch();
        if (flushed !== null) {
          const target = flushed.find((row) => row.ownerNickname === null);
          if (target !== undefined) {
            chooseAddress(target.address);
          }
          return;
        }
        if (rovingIndex >= 0) {
          handleToggleAddress(rovingIndex);
        }
        return;
      }
      if (e.key === 'Escape' && searchInput !== '') {
        e.stopPropagation();
        handleClearSearch();
      }
    },
    [
      flushSearch,
      chooseAddress,
      moveActive,
      nextEnabledIndex,
      pickerRows.length,
      rovingIndex,
      handleToggleAddress,
      searchInput,
      handleClearSearch
    ]
  );

  /**
   * Adopts the nickname as the server now holds it after a save was turned down, and takes its
   * stamp with it so the next attempt is checked against that copy rather than the refused one.
   * The chosen and removed addresses are left alone: they are the editor's own work.
   */
  const reseedFromServerCopy = useCallback((current: ClientGroup): void => {
    setNickname(current.nickname);
    setDescription(current.description || '');
    setSeparateMemberRows(current.separateMemberRows);
    expectedUpdatedAtRef.current = current.updatedAtUtc ?? null;
  }, []);

  const handleSubmit = useCallback(
    async (e: React.FormEvent): Promise<void> => {
      e.preventDefault();
      setError(null);
      setRejectedIps([]);

      const trimmedNickname = nickname.trim();
      if (!trimmedNickname) {
        setError(t('modals.clientGroup.errors.nicknameRequired'));
        return;
      }
      // A nickname with no addresses renders a blank meta line in Management and drops off
      // both stats surfaces, and the server does not refuse an empty list.
      if (pendingMemberIps.length === 0) {
        setError(t('modals.clientGroup.errors.needsOneAddress'));
        return;
      }

      setSaving(true);
      try {
        if (savedGroupId !== null) {
          // The fields are written first and writing them moves the stamp, so this is the one
          // place the copy the session started from can still be compared against what the
          // server holds. A nickname someone else changed since is refused here, before this
          // save can adopt their version of it.
          const saved = await updateClientGroup(savedGroupId, {
            nickname: trimmedNickname,
            description: description.trim() || undefined,
            separateMemberRows,
            expectedUpdatedAtUtc: expectedUpdatedAtRef.current
          });
          if (saved.status === 'stale') {
            reseedFromServerCopy(saved.currentGroup);
            setError(t('modals.clientGroup.errors.changedElsewhere'));
            return;
          }
          expectedUpdatedAtRef.current = saved.updatedAtUtc ?? null;
          if (removedIps.length > 0 || chosenList.length > 0) {
            // One request carries the whole desired membership, so a partial apply cannot
            // leave earlier addresses committed with no way back.
            // Routed through the context so the saved membership reloads on its own instead of
            // waiting for a socket echo that a disconnected tab never receives.
            const result = await setMembers(
              savedGroupId,
              pendingMemberIps,
              expectedUpdatedAtRef.current
            );
            if (result.status === 'stale') {
              // The list is the whole membership, so saving it over a copy that moved would drop
              // whatever the other editor did with nothing to show for it. Take what the server
              // now holds and let the editor look at their own pending changes again.
              reseedFromServerCopy(result.currentGroup);
              setError(t('modals.clientGroup.errors.changedElsewhere'));
              return;
            }
            expectedUpdatedAtRef.current = result.group.updatedAtUtc ?? null;
            if (result.rejectedIps.length > 0) {
              setRejectedIps(result.rejectedIps);
              setChosenMany(result.rejectedIps, false);
              return;
            }
          }
          const ipsAdded =
            chosenList.length > 0
              ? t('modals.clientGroup.messages.andAddedIps', { count: chosenList.length })
              : '';
          onSuccess(
            t('modals.clientGroup.messages.updatedNickname', {
              nickname: trimmedNickname,
              ipsAdded
            })
          );
        } else {
          const created = await createClientGroup({
            nickname: trimmedNickname,
            description: description.trim() || undefined,
            initialIps: pendingMemberIps,
            separateMemberRows
          });
          const rejected = created.rejectedIps;
          if (created.status === 'rejected') {
            // Not one address could be taken, so the nickname was rolled back and there is
            // nothing on the server to edit: no id is kept, or the next Save would write to a
            // group that does not exist.
            setChosenMany(rejected, false);
            setError(
              t('modals.clientGroup.errors.noAddressesAccepted', { addresses: rejected.join(', ') })
            );
            return;
          }
          if (rejected.length > 0) {
            setCreatedGroupId(created.id);
            // The dialog stays open on this one, and the next Save edits the nickname that now
            // exists, so the session carries on from the copy the create handed back.
            expectedUpdatedAtRef.current = created.updatedAtUtc ?? null;
            setRejectedIps(rejected);
            setChosenMany(rejected, false);
            return;
          }
          onSuccess(t('modals.clientGroup.messages.addedNickname', { nickname: trimmedNickname }));
        }
        onClose();
      } catch (err) {
        console.error('Failed to save client nickname:', getErrorMessage(err));
        setError(t('modals.clientGroup.errors.failedToSave'));
      } finally {
        setSaving(false);
      }
    },
    [
      t,
      nickname,
      description,
      separateMemberRows,
      pendingMemberIps,
      removedIps,
      chosenList,
      savedGroupId,
      createClientGroup,
      updateClientGroup,
      setMembers,
      setChosenMany,
      reseedFromServerCopy,
      onClose,
      onSuccess
    ]
  );

  if (!isOpen) return null;

  const renderPickerBody = (): React.ReactNode => {
    if (groupsError) {
      return (
        <div className="clientgroup-ip-picker__state">
          <Alert color="red">
            <span className="text-sm">{t('modals.clientGroup.errors.loadAddressesFailed')}</span>
          </Alert>
          <Button type="button" size="sm" variant="default" onClick={() => void refreshGroups()}>
            {t('common.retry')}
          </Button>
        </div>
      );
    }
    if (groupsLoading) {
      return (
        <div className="clientgroup-ip-picker__state">
          <LoadingState rows={4} />
        </div>
      );
    }
    // An install with an empty client list still has a working picker, because an address can be
    // typed straight into the box. The "nothing to choose from" state only stands while the box
    // is not offering one.
    if (addressableCount === 0 && matchingRows.length === 0) {
      return (
        <EmptyState
          variant="plain"
          icon={Users}
          title={
            knownIps.length === 0
              ? t('modals.clientGroup.emptyStates.noAddressesYet')
              : t('modals.clientGroup.emptyStates.everyAddressNamed')
          }
          subtitle={t('modals.clientGroup.emptyStates.everyAddressNamedHint')}
        />
      );
    }
    if (matchingRows.length === 0) {
      return (
        <div className="clientgroup-ip-picker__state">
          <p className="text-sm text-themed-muted">
            {t('modals.clientGroup.emptyStates.noAddressMatches')}
          </p>
          <Button type="button" size="sm" variant="default" onClick={handleClearSearch}>
            {t('modals.clientGroup.actions.clearSearch')}
          </Button>
        </div>
      );
    }
    if (pickerRows.length === 0) {
      return (
        <div className="clientgroup-ip-picker__state">
          <p className="text-sm text-themed-muted">
            {t('modals.clientGroup.emptyStates.allMatchingChosen')}
          </p>
        </div>
      );
    }
    return (
      <CustomScrollbar
        variant="rail"
        radius="none"
        paddingMode="compact"
        maxHeight={PICKER_MAX_HEIGHT}
      >
        <div
          role="listbox"
          aria-multiselectable="true"
          aria-label={t('modals.clientGroup.labels.addresses')}
          aria-busy={saving}
          className={`clientgroup-ip-rows divided-list${saving ? ' clientgroup-ip-rows--busy' : ''}`}
        >
          {pickerRows.map((row, index) => {
            const owned = row.ownerNickname !== null;
            // The same label the chips carry, so an address reads the same before and after it is
            // picked. A name a lookup just found outranks the reverse-name map, which only covers
            // addresses the install has already seen. The address stays on the row underneath,
            // because it is what the row is really about.
            const rowLabel = resolveClientLabel(
              row.address,
              null,
              lookupNames[row.address] ?? getHostnameForIp(row.address)
            ).text;
            return (
              <button
                key={row.address}
                ref={(node) => {
                  if (node) {
                    rowRefs.current.set(row.address, node);
                  } else {
                    rowRefs.current.delete(row.address);
                  }
                }}
                type="button"
                role="option"
                aria-selected={false}
                aria-disabled={owned || undefined}
                tabIndex={!owned && !saving && index === rovingIndex ? 0 : -1}
                onClick={(e) => handleRowClick(index, e)}
                onKeyDown={(e) => handleRowKeyDown(index, e)}
                onFocus={() => {
                  if (!owned) setActiveAddress(row.address);
                }}
                className={`mgmt-row mgmt-row--interactive focus-ring--inset clientgroup-ip-row w-full text-left${
                  index === activeIndex ? ' clientgroup-ip-row--active' : ''
                }${owned ? ' clientgroup-ip-row--owned' : ''}`}
              >
                <span className="mgmt-row__body">
                  <span
                    className={`mgmt-row__title truncate${rowLabel === row.address ? ' font-mono' : ''}`}
                  >
                    {rowLabel}
                  </span>
                  {rowLabel !== row.address && (
                    <span className="mgmt-row__meta font-mono">{row.address}</span>
                  )}
                  {owned && (
                    <span className="mgmt-row__meta">
                      {t('modals.clientGroup.messages.alreadyNamed', {
                        nickname: row.ownerNickname
                      })}
                    </span>
                  )}
                  {!owned && row.unseen && (
                    <span className="mgmt-row__meta">
                      {t('modals.clientGroup.messages.notSeenOnNetwork')}
                    </span>
                  )}
                </span>
                {!owned && <Plus className="w-3.5 h-3.5 flex-shrink-0 text-themed-muted" />}
              </button>
            );
          })}
        </div>
      </CustomScrollbar>
    );
  };

  /** What the last lookup came to, in one line under the box that started it. */
  const renderLookupNote = (): React.ReactNode => {
    if (lookup.status === 'idle' || lookup.status === 'looking') return null;

    if (lookup.status === 'failed') {
      return (
        <p className="clientgroup-lookup-note text-themed-error">
          {t('modals.clientGroup.lookup.failed', { hostname: lookup.hostname })}
        </p>
      );
    }

    if (lookup.status === 'empty') {
      const reasonKey = lookupReasonKeys[lookup.reason];
      if (reasonKey === null) return null;
      return (
        <p className="clientgroup-lookup-note text-themed-error">
          {t(reasonKey, { hostname: lookup.hostname })}
        </p>
      );
    }

    // The name resolved to addresses this nickname already holds. Saying nothing here would leave
    // the lookup looking like it did not run.
    if (lookup.added.length === 0 && lookup.ownedElsewhere.length === 0) {
      return (
        <p className="clientgroup-lookup-note text-themed-muted">
          {t('modals.clientGroup.lookup.alreadyHeld', { hostname: lookup.hostname })}
        </p>
      );
    }

    return (
      <p className="clientgroup-lookup-note text-themed-muted">
        {lookup.added.length > 0 &&
          t('modals.clientGroup.lookup.added', {
            hostname: lookup.hostname,
            addresses: lookup.added.join(', ')
          })}
        {lookup.ownedElsewhere.length > 0 && (
          <span className="clientgroup-lookup-note__clash">
            {t('modals.clientGroup.lookup.ownedElsewhere', {
              addresses: lookup.ownedElsewhere.join(', ')
            })}
          </span>
        )}
      </p>
    );
  };

  return (
    <Modal
      opened={isOpen}
      onClose={onClose}
      title={isEditing ? t('modals.clientGroup.editTitle') : t('modals.clientGroup.addTitle')}
    >
      <form
        ref={formRef}
        tabIndex={-1}
        onSubmit={handleSubmit}
        className="clientgroup-form space-y-4"
      >
        {error && (
          <Alert color="red">
            <span className="text-sm">{error}</span>
          </Alert>
        )}

        {rejectedIps.length > 0 && (
          <Alert color="red">
            <span className="text-sm">
              {t('modals.clientGroup.errors.addressesRejected', {
                addresses: rejectedIps.join(', ')
              })}
            </span>
          </Alert>
        )}

        {/* Nickname */}
        <div>
          <FormField label={t('modals.clientGroup.labels.nickname')} required>
            {(field) => (
              <input
                {...field}
                type="text"
                value={nickname}
                onChange={(e) => setNickname(e.target.value)}
                className="w-full px-3 py-2 border text-themed-primary text-sm themed-input control-h-md"
                placeholder={t('modals.clientGroup.placeholders.name')}
                required
                autoFocus
              />
            )}
          </FormField>
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
            className="w-full px-3 py-2 border text-themed-primary text-sm resize-none themed-input"
            placeholder={t('modals.clientGroup.placeholders.description')}
            rows={2}
          />
        </div>

        {/* How this nickname reports in the client stats. Shown for every group, including a
            one-IP one, so the choice is made once at creation rather than appearing later. */}
        <div>
          <Tooltip
            content={t('modals.clientGroup.labels.rowModeHelp')}
            position="bottom"
            className="inline-block"
          >
            <span id="clientgroup-row-mode-label" className="form-field-label">
              {t('modals.clientGroup.labels.rowMode')}
            </span>
          </Tooltip>
          <div role="group" aria-labelledby="clientgroup-row-mode-label">
            <SegmentedControl
              options={[
                {
                  value: ROW_MODE_COMBINED,
                  label: t('modals.clientGroup.mode.combined'),
                  tooltip: t('modals.clientGroup.mode.combinedTooltip')
                },
                {
                  value: ROW_MODE_SEPARATE,
                  label: t('modals.clientGroup.mode.separate'),
                  tooltip: t('modals.clientGroup.mode.separateTooltip')
                }
              ]}
              value={separateMemberRows ? ROW_MODE_SEPARATE : ROW_MODE_COMBINED}
              onChange={(value) => setSeparateMemberRows(value === ROW_MODE_SEPARATE)}
              size="md"
              showLabels
              fullWidth
            />
          </div>
        </div>

        {/* Addresses: what the nickname will hold after Save, then the picker that feeds it. */}
        <div>
          <label className="form-field-label">
            {t('modals.clientGroup.labels.addresses')}{' '}
            <Badge variant="neutral" className="badge-count">
              {pendingMemberIps.length}
            </Badge>{' '}
            <span className="text-themed-muted">
              ({t('modals.clientGroup.labels.addressesHint')})
            </span>{' '}
            {searchQuery !== '' && (
              <Badge variant="neutral" className="badge-count">
                {t('modals.clientGroup.messages.matchCount', {
                  shown: seenMatchCount,
                  total: addressableCount
                })}
              </Badge>
            )}
          </label>

          {pendingMemberIps.length + removedIps.length > 0 && (
            <div className="flex flex-wrap gap-2 mb-3">
              {/* The nickname this member belongs to is already the dialog's own title, so the
                  chip surfaces the machine's own hostname instead of repeating it. */}
              {currentMemberIps.map((ip) => {
                const marked = removedSet.has(ip);
                return (
                  <ClientAddressChip
                    key={ip}
                    ip={ip}
                    hostname={getHostnameForIp(ip)}
                    state={marked ? 'removing' : 'current'}
                    onRemove={() => handleToggleRemoval(ip)}
                    removeLabel={
                      marked ? t('modals.clientGroup.actions.undoRemove') : t('common.remove')
                    }
                    disabled={saving}
                  />
                );
              })}
              {/* A name a lookup found outranks the reverse-name map, which only covers addresses
                  the install has already seen and so has nothing for the machine just added. */}
              {chosenList.map((ip) => (
                <ClientAddressChip
                  key={ip}
                  ip={ip}
                  hostname={lookupNames[ip] ?? getHostnameForIp(ip)}
                  state="added"
                  onRemove={() => toggleChosen(ip)}
                  disabled={saving}
                  // An address nothing has downloaded from shows no stats once it is saved, so it
                  // says why here rather than looking like a nickname that quietly does nothing.
                  note={
                    knownIpSet.has(ip) ? undefined : t('modals.clientGroup.messages.notSeenYet')
                  }
                />
              ))}
            </div>
          )}

          {pendingMemberIps.length === 0 && (
            <Alert color="red" className="mb-3">
              <span className="text-sm">{t('modals.clientGroup.errors.needsOneAddress')}</span>
            </Alert>
          )}

          {selectableAddresses.length > 0 && (
            <div className="clientgroup-picker-toolbar">
              <Checkbox
                checked={allMatchingChosen}
                onChange={handleSelectAllToggle}
                disabled={saving}
                label={t('modals.clientGroup.actions.addAllMatching', {
                  count: selectableAddresses.length
                })}
              />
              {chosen.count > 0 && (
                <Button
                  type="button"
                  variant="transparent"
                  size="sm"
                  onClick={clearChosen}
                  disabled={saving}
                  className="clientgroup-picker-clear themed-border-radius-sm focus-ring text-sm"
                >
                  {t('common.clear')}
                </Button>
              )}
            </div>
          )}

          <div className="mb-2">
            <SearchInput
              value={searchInput}
              onChange={handleSearchChange}
              onKeyDown={handleSearchKeyDown}
              // A save disables the box and freezes the rows under it, so the clear control goes
              // with them rather than staying live beside a control that takes no input.
              onClear={saving ? undefined : handleClearSearch}
              ref={searchRef}
              disabled={saving}
              placeholder={t('modals.clientGroup.placeholders.searchAddresses')}
              aria-label={t('modals.clientGroup.placeholders.searchAddresses')}
            />
          </div>

          {/* A name cannot be matched against a list of addresses, so it gets an action instead of
              a row: the network is asked what it resolves to, and the answer is what gets picked. */}
          {lookupCandidate !== null && (
            <div className="clientgroup-lookup">
              <Button
                type="button"
                size="sm"
                variant="default"
                onClick={() => void handleLookupHostname()}
                loading={lookup.status === 'looking'}
                disabled={saving || lookup.status === 'looking'}
              >
                {t('modals.clientGroup.actions.lookUpHostname', { hostname: lookupCandidate })}
              </Button>
            </div>
          )}

          {renderLookupNote()}

          <div className="mgmt-list divided-list clientgroup-ip-picker">{renderPickerBody()}</div>
        </div>

        {/* Actions */}
        <div className="flex flex-col-reverse sm:flex-row sm:justify-end gap-3 mt-4 pt-4 border-t border-themed-primary">
          <Button
            type="button"
            variant="default"
            onClick={onClose}
            disabled={saving}
            className="min-h-[44px] sm:min-h-10"
          >
            {t('common.cancel')}
          </Button>
          <Button
            type="submit"
            variant="filled"
            color="primary"
            loading={saving}
            disabled={saving || !nickname.trim() || pendingMemberIps.length === 0}
            className="min-h-[44px] sm:min-h-10"
          >
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

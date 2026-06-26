import React from 'react';
import { XboxAuthModal } from '@components/modals/auth/XboxAuthModal';
import type { XboxAuthState, XboxAuthActions } from '@hooks/useXboxMappingAuth';

interface XboxMappingLoginModalProps {
  opened: boolean;
  onClose: () => void;
  state: XboxAuthState;
  actions: XboxAuthActions;
  onCancelLogin?: () => void;
}

// Thin adapter: re-points XboxAuthModal from the prefill-daemon state stack
// (usePrefillSignalR + usePrefillSteamAuth) to the manager-side useXboxMappingAuth hook.
// XboxAuthModal consumes only the device-code slice, which XboxAuthState/XboxAuthActions
// satisfy directly. Completion arrives via SignalR XboxMappingProgress in the hook;
// onCancelLogin stops a pending poll server-side when the modal is closed.
const XboxMappingLoginModal: React.FC<XboxMappingLoginModalProps> = ({
  opened,
  onClose,
  state,
  actions,
  onCancelLogin
}) => (
  <XboxAuthModal
    opened={opened}
    onClose={onClose}
    state={state}
    actions={actions}
    onCancelLogin={onCancelLogin}
  />
);

export default XboxMappingLoginModal;

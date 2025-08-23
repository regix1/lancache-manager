import * as react from 'react';
import { ContextType } from './use-avatar-group.mjs';
import '@heroui/system';
import '@heroui/theme';
import '@heroui/react-utils';
import './avatar.mjs';
import './use-avatar.mjs';
import 'tailwind-variants';

declare const AvatarGroupProvider: react.Provider<ContextType>;
declare const useAvatarGroupContext: () => ContextType;

export { AvatarGroupProvider, useAvatarGroupContext };

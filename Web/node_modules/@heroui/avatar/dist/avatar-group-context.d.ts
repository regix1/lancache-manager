import * as react from 'react';
import { ContextType } from './use-avatar-group.js';
import '@heroui/system';
import '@heroui/theme';
import '@heroui/react-utils';
import './avatar.js';
import './use-avatar.js';
import 'tailwind-variants';

declare const AvatarGroupProvider: react.Provider<ContextType>;
declare const useAvatarGroupContext: () => ContextType;

export { AvatarGroupProvider, useAvatarGroupContext };

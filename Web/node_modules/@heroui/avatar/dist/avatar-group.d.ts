import * as _heroui_system from '@heroui/system';
import { UseAvatarGroupProps } from './use-avatar-group.js';
import 'react';
import '@heroui/theme';
import '@heroui/react-utils';
import './avatar.js';
import './use-avatar.js';
import 'tailwind-variants';

interface AvatarGroupProps extends UseAvatarGroupProps {
}
declare const AvatarGroup: _heroui_system.InternalForwardRefRenderFunction<"div", AvatarGroupProps, never>;

export { type AvatarGroupProps, AvatarGroup as default };

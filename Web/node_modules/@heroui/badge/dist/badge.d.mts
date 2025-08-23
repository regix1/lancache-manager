import * as _heroui_system from '@heroui/system';
import { UseBadgeProps } from './use-badge.mjs';
import 'tailwind-variants';
import '@heroui/theme';
import 'react';
import '@heroui/react-utils';

interface BadgeProps extends UseBadgeProps {
}
declare const Badge: _heroui_system.InternalForwardRefRenderFunction<"span", BadgeProps, never>;

export { type BadgeProps, Badge as default };

import * as _heroui_system from '@heroui/system';
import { UseCardProps } from './use-card.mjs';
import 'react';
import '@react-types/shared';
import '@heroui/theme';
import '@heroui/ripple';
import '@react-aria/interactions';
import '@heroui/react-utils';

interface CardProps extends UseCardProps {
}
declare const Card: _heroui_system.InternalForwardRefRenderFunction<"div", CardProps, never>;

export { type CardProps, Card as default };

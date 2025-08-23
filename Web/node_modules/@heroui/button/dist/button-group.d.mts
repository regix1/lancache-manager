import * as _heroui_system from '@heroui/system';
import { UseButtonGroupProps } from './use-button-group.mjs';
import 'react';
import './button.mjs';
import './use-button.mjs';
import '@heroui/theme';
import '@heroui/use-aria-button';
import '@heroui/ripple';
import '@heroui/react-utils';

interface ButtonGroupProps extends UseButtonGroupProps {
}
declare const ButtonGroup: _heroui_system.InternalForwardRefRenderFunction<"div", ButtonGroupProps, never>;

export { type ButtonGroupProps, ButtonGroup as default };

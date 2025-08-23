import * as _heroui_system from '@heroui/system';
import { UseButtonProps } from './use-button.js';
import 'react';
import '@heroui/theme';
import '@heroui/use-aria-button';
import '@heroui/ripple';
import '@heroui/react-utils';

interface ButtonProps extends UseButtonProps {
}
declare const Button: _heroui_system.InternalForwardRefRenderFunction<"button", ButtonProps, never>;

export { type ButtonProps, Button as default };

import * as _heroui_system from '@heroui/system';
import { UseToastProps } from './use-toast.mjs';
import 'tailwind-variants';
import 'react';
import '@heroui/theme';
import '@heroui/react-utils';
import '@react-aria/toast';
import '@react-stately/toast';
import 'framer-motion';

interface ToastProps extends UseToastProps {
}
declare const Toast: _heroui_system.InternalForwardRefRenderFunction<"div", ToastProps, never>;

export { type ToastProps, Toast as default };

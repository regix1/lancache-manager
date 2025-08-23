import * as _heroui_system from '@heroui/system';
import { UseCircularProgressProps } from './use-circular-progress.js';
import 'tailwind-variants';
import 'react';
import '@heroui/theme';
import '@react-types/progress';
import '@heroui/react-utils';

interface CircularProgressProps extends UseCircularProgressProps {
}
declare const CircularProgress: _heroui_system.InternalForwardRefRenderFunction<"div", CircularProgressProps, never>;

export { type CircularProgressProps, CircularProgress as default };

import * as _heroui_system from '@heroui/system';
import { UseProgressProps } from './use-progress.js';
import 'tailwind-variants';
import 'react';
import '@heroui/theme';
import '@react-types/progress';
import '@heroui/react-utils';

interface ProgressProps extends UseProgressProps {
}
declare const Progress: _heroui_system.InternalForwardRefRenderFunction<"div", ProgressProps, never>;

export { type ProgressProps, Progress as default };

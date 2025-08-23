import * as _heroui_system from '@heroui/system';
import { UseCheckboxProps } from './use-checkbox.js';
import '@heroui/theme';
import '@react-types/checkbox';
import 'react';

interface CheckboxProps extends UseCheckboxProps {
}
declare const Checkbox: _heroui_system.InternalForwardRefRenderFunction<"input", CheckboxProps, never>;

export { type CheckboxProps, Checkbox as default };

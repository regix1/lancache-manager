import * as _heroui_system from '@heroui/system';
import { UseCheckboxGroupProps } from './use-checkbox-group.js';
import 'react';
import '@heroui/theme';
import '@react-types/checkbox';
import '@react-types/shared';
import '@heroui/react-utils';
import '@react-stately/checkbox';
import './checkbox.js';
import './use-checkbox.js';

interface CheckboxGroupProps extends UseCheckboxGroupProps {
}
declare const CheckboxGroup: _heroui_system.InternalForwardRefRenderFunction<"div", CheckboxGroupProps, never>;

export { type CheckboxGroupProps, CheckboxGroup as default };

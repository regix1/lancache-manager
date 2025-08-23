import * as _heroui_system from '@heroui/system';
import { UseRadioGroupProps } from './use-radio-group.js';
import 'react';
import '@react-types/radio';
import '@react-types/shared';
import '@heroui/react-utils';
import '@heroui/theme';
import '@react-stately/radio';
import './radio.js';
import './use-radio.js';

interface RadioGroupProps extends Omit<UseRadioGroupProps, "defaultChecked"> {
}
declare const RadioGroup: _heroui_system.InternalForwardRefRenderFunction<"div", RadioGroupProps, never>;

export { type RadioGroupProps, RadioGroup as default };

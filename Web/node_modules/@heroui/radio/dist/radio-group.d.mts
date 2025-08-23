import * as _heroui_system from '@heroui/system';
import { UseRadioGroupProps } from './use-radio-group.mjs';
import 'react';
import '@react-types/radio';
import '@react-types/shared';
import '@heroui/react-utils';
import '@heroui/theme';
import '@react-stately/radio';
import './radio.mjs';
import './use-radio.mjs';

interface RadioGroupProps extends Omit<UseRadioGroupProps, "defaultChecked"> {
}
declare const RadioGroup: _heroui_system.InternalForwardRefRenderFunction<"div", RadioGroupProps, never>;

export { type RadioGroupProps, RadioGroup as default };

import * as _heroui_system from '@heroui/system';
import { UseInputProps } from './use-input.js';
import 'react';
import '@heroui/theme';
import '@react-types/textfield';

interface InputProps extends Omit<UseInputProps, "isMultiline"> {
}
declare const Input: _heroui_system.InternalForwardRefRenderFunction<"input", InputProps, never>;

export { type InputProps, Input as default };

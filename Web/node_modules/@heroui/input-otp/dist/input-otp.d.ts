import * as _heroui_system from '@heroui/system';
import { UseInputOtpProps } from './use-input-otp.js';
import 'input-otp';
import 'tailwind-variants';
import 'react';
import '@heroui/theme';
import '@heroui/react-utils';
import '@react-types/textfield';

interface InputOtpProps extends UseInputOtpProps {
}
declare const InputOtp: _heroui_system.InternalForwardRefRenderFunction<"input", InputOtpProps, never>;

export { type InputOtpProps, InputOtp as default };

import * as react_jsx_runtime from 'react/jsx-runtime';
import { AriaButtonProps } from '@react-types/button';
import { ButtonProps } from '@heroui/button';

interface NumberInputStepperProps extends Omit<ButtonProps, keyof AriaButtonProps> {
    direction: "up" | "down";
}
declare const NumberInputStepper: {
    ({ direction, ...otherProps }: NumberInputStepperProps): react_jsx_runtime.JSX.Element;
    displayName: string;
};

export { type NumberInputStepperProps, NumberInputStepper as default };

import * as react_jsx_runtime from 'react/jsx-runtime';
import { AriaButtonProps } from '@react-types/button';
import { HTMLHeroUIProps, As } from '@heroui/system';
import { ButtonProps } from '@heroui/button';
import { ReactNode, HTMLAttributes, RefObject } from 'react';

interface CalendarBaseProps extends HTMLHeroUIProps<"div"> {
    Component?: As;
    showHelper?: boolean;
    topContent?: ReactNode;
    bottomContent?: ReactNode;
    calendarProps: HTMLAttributes<HTMLElement>;
    nextButtonProps: AriaButtonProps;
    prevButtonProps: AriaButtonProps;
    buttonPickerProps?: ButtonProps;
    errorMessageProps: HTMLAttributes<HTMLElement>;
    calendarRef: RefObject<HTMLDivElement>;
    errorMessage?: ReactNode;
    firstDayOfWeek?: "sun" | "mon" | "tue" | "wed" | "thu" | "fri" | "sat";
}
declare function CalendarBase(props: CalendarBaseProps): react_jsx_runtime.JSX.Element;

export { CalendarBase, type CalendarBaseProps };

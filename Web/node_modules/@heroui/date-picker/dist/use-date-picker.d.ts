import * as react from 'react';
import { DateValue } from '@internationalized/date';
import { DateInputProps } from '@heroui/date-input';
import { DatePickerState } from '@react-stately/datepicker';
import { ButtonProps } from '@heroui/button';
import { CalendarProps } from '@heroui/calendar';
import { PopoverProps } from '@heroui/popover';
import { UseDatePickerBaseProps } from './use-date-picker-base.js';
import { DOMAttributes } from '@heroui/system';
import { SlotsToClasses, DatePickerSlots } from '@heroui/theme';
import { AriaDatePickerProps } from '@react-aria/datepicker';
import '@react-types/datepicker';
import '@react-types/shared';

interface Props<T extends DateValue> extends UseDatePickerBaseProps<T> {
}
interface Props<T extends DateValue> extends Omit<UseDatePickerBaseProps<T>, keyof AriaDatePickerProps<T>> {
    /**
     * Classname or List of classes to change the classNames of the element.
     * if `className` is passed, it will be added to the base slot.
     *
     * @example
     * ```ts
     * <DatePicker classNames={{
     *    base:"base-classes",
     *    label: "label-classes",
     *    calendar:"calendar-classes",
     *    selectorButton:"selector-button-classes",
     *    selectorIcon:"selector-icon-classes",
     *    popoverContent:"popover-content-classes",
     *    calendarContent : "calendar-content-classes",
     *    inputWrapper: "input-wrapper-classes",
     *    input: "input-classes",
     *    segment: "segment-classes",
     *    helperWrapper: "helper-wrapper-classes",
     *    description: "description-classes",
     *    errorMessage: "error-message-classes",
     * }} />
     * ```
     */
    classNames?: SlotsToClasses<DatePickerSlots> & DateInputProps<T>["classNames"];
}
type UseDatePickerProps<T extends DateValue> = Props<T> & AriaDatePickerProps<T>;
declare function useDatePicker<T extends DateValue>({ className, classNames, ...originalProps }: UseDatePickerProps<T>): {
    state: DatePickerState;
    startContent: react.ReactNode;
    endContent: react.ReactNode;
    selectorIcon: react.ReactNode;
    showTimeField: boolean;
    isCalendarHeaderExpanded: boolean | undefined;
    disableAnimation: boolean;
    CalendarTopContent: react.ReactNode;
    CalendarBottomContent: react.ReactNode;
    getDateInputProps: () => DateInputProps;
    getPopoverProps: (props?: DOMAttributes) => PopoverProps;
    getSelectorButtonProps: () => ButtonProps;
    getCalendarProps: () => CalendarProps;
    getTimeInputProps: () => {};
    getSelectorIconProps: () => {
        className: string;
        "data-slot": string;
    };
};
type UseDatePickerReturn = ReturnType<typeof useDatePicker>;

export { type UseDatePickerProps, type UseDatePickerReturn, useDatePicker };

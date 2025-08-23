import * as react from 'react';
import * as _heroui_theme from '@heroui/theme';
import * as tailwind_variants from 'tailwind-variants';
import * as _react_stately_calendar from '@react-stately/calendar';
import { CalendarDate } from '@internationalized/date';
import { PressEvent } from '@react-types/shared';
import { HTMLHeroUIProps } from '@heroui/system';

type PickerValue = {
    value: string;
    label: string;
};
interface CalendarPickerProps extends HTMLHeroUIProps<"div"> {
    date: CalendarDate;
    currentMonth: CalendarDate;
}
type CalendarPickerListType = "months" | "years";
declare function useCalendarPicker(props: CalendarPickerProps): {
    state: _react_stately_calendar.CalendarState | _react_stately_calendar.RangeCalendarState;
    slots: ({
        base: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        prevButton: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        nextButton: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        headerWrapper: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        header: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        title: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        content: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        gridWrapper: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        grid: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        gridHeader: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        gridHeaderRow: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        gridHeaderCell: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        gridBody: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        gridBodyRow: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        cell: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        cellButton: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        pickerWrapper: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        pickerMonthList: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        pickerYearList: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        pickerHighlight: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        pickerItem: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        helperWrapper: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        errorMessage: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
    } & {
        base: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        prevButton: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        nextButton: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        headerWrapper: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        header: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        title: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        content: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        gridWrapper: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        grid: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        gridHeader: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        gridHeaderRow: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        gridHeaderCell: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        gridBody: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        gridBodyRow: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        cell: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        cellButton: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        pickerWrapper: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        pickerMonthList: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        pickerYearList: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        pickerHighlight: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        pickerItem: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        helperWrapper: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
        errorMessage: (slotProps?: ({
            color?: "primary" | "foreground" | "secondary" | "success" | "warning" | "danger" | undefined;
            showShadow?: boolean | undefined;
            hideDisabledDates?: boolean | undefined;
            showMonthAndYearPickers?: boolean | undefined;
            isRTL?: boolean | undefined;
            isRange?: boolean | undefined;
            isHeaderWrapperExpanded?: boolean | undefined;
            disableAnimation?: boolean | undefined;
        } & tailwind_variants.ClassProp<tailwind_variants.ClassValue>) | undefined) => string;
    } & {}) | undefined;
    classNames: _heroui_theme.SlotsToClasses<"base" | "header" | "title" | "content" | "errorMessage" | "gridWrapper" | "grid" | "gridHeader" | "gridHeaderRow" | "gridHeaderCell" | "gridBody" | "gridBodyRow" | "cell" | "pickerWrapper" | "pickerMonthList" | "pickerYearList" | "pickerHighlight" | "helperWrapper" | "prevButton" | "nextButton" | "headerWrapper" | "cellButton" | "pickerItem"> | undefined;
    years: {
        value: number;
        label: string;
    }[];
    months: {
        value: number;
        label: string;
    }[];
    highlightRef: react.RefObject<HTMLDivElement>;
    monthsListRef: react.RefObject<HTMLDivElement>;
    yearsListRef: react.RefObject<HTMLDivElement>;
    getItemRef: (node: HTMLElement | null, value: number, list: CalendarPickerListType) => void;
    isHeaderExpanded: boolean | undefined;
    onPickerItemPressed: (e: PressEvent, list: CalendarPickerListType) => void;
    onPickerItemKeyDown: (e: React.KeyboardEvent<HTMLElement>, value: number, list: CalendarPickerListType) => void;
};
type UseCalendarPickerReturn = ReturnType<typeof useCalendarPicker>;

export { type CalendarPickerProps, type PickerValue, type UseCalendarPickerReturn, useCalendarPicker };

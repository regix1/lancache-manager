import * as tailwind_variants from 'tailwind-variants';
import { VariantProps } from 'tailwind-variants';

declare const calendar: tailwind_variants.TVReturnType<{
    color: {
        foreground: {};
        primary: {};
        secondary: {};
        success: {};
        warning: {};
        danger: {};
    };
    isRange: {
        true: {
            cellButton: string[];
        };
        false: {};
    };
    hideDisabledDates: {
        true: {
            cellButton: string;
        };
        false: {};
    };
    isHeaderWrapperExpanded: {
        true: {
            headerWrapper: string[];
            pickerWrapper: string;
            gridWrapper: string;
            grid: string;
            nextButton: string;
            prevButton: string;
        };
        false: {};
    };
    showMonthAndYearPickers: {
        true: {
            base: string;
            header: string;
        };
        false: {};
    };
    showShadow: {
        true: {
            cellButton: string;
        };
        false: {
            cellButton: string;
        };
    };
    disableAnimation: {
        true: {
            cellButton: string;
        };
        false: {
            headerWrapper: string[];
            grid: string;
            cellButton: string[];
            pickerWrapper: string;
            pickerItem: string;
        };
    };
    isRTL: {
        true: {
            nextButton: string;
            prevButton: string;
        };
        false: {};
    };
}, {
    base: string[];
    prevButton: string[];
    nextButton: string[];
    headerWrapper: string[];
    header: string;
    title: string;
    content: string;
    gridWrapper: string;
    grid: string;
    gridHeader: string;
    gridHeaderRow: string;
    gridHeaderCell: string;
    gridBody: string;
    gridBodyRow: string;
    cell: string;
    cellButton: string[];
    pickerWrapper: string;
    pickerMonthList: string;
    pickerYearList: string;
    pickerHighlight: string;
    pickerItem: string[];
    helperWrapper: string;
    errorMessage: string;
}, undefined, {
    color: {
        foreground: {};
        primary: {};
        secondary: {};
        success: {};
        warning: {};
        danger: {};
    };
    isRange: {
        true: {
            cellButton: string[];
        };
        false: {};
    };
    hideDisabledDates: {
        true: {
            cellButton: string;
        };
        false: {};
    };
    isHeaderWrapperExpanded: {
        true: {
            headerWrapper: string[];
            pickerWrapper: string;
            gridWrapper: string;
            grid: string;
            nextButton: string;
            prevButton: string;
        };
        false: {};
    };
    showMonthAndYearPickers: {
        true: {
            base: string;
            header: string;
        };
        false: {};
    };
    showShadow: {
        true: {
            cellButton: string;
        };
        false: {
            cellButton: string;
        };
    };
    disableAnimation: {
        true: {
            cellButton: string;
        };
        false: {
            headerWrapper: string[];
            grid: string;
            cellButton: string[];
            pickerWrapper: string;
            pickerItem: string;
        };
    };
    isRTL: {
        true: {
            nextButton: string;
            prevButton: string;
        };
        false: {};
    };
}, {
    base: string[];
    prevButton: string[];
    nextButton: string[];
    headerWrapper: string[];
    header: string;
    title: string;
    content: string;
    gridWrapper: string;
    grid: string;
    gridHeader: string;
    gridHeaderRow: string;
    gridHeaderCell: string;
    gridBody: string;
    gridBodyRow: string;
    cell: string;
    cellButton: string[];
    pickerWrapper: string;
    pickerMonthList: string;
    pickerYearList: string;
    pickerHighlight: string;
    pickerItem: string[];
    helperWrapper: string;
    errorMessage: string;
}, tailwind_variants.TVReturnType<{
    color: {
        foreground: {};
        primary: {};
        secondary: {};
        success: {};
        warning: {};
        danger: {};
    };
    isRange: {
        true: {
            cellButton: string[];
        };
        false: {};
    };
    hideDisabledDates: {
        true: {
            cellButton: string;
        };
        false: {};
    };
    isHeaderWrapperExpanded: {
        true: {
            headerWrapper: string[];
            pickerWrapper: string;
            gridWrapper: string;
            grid: string;
            nextButton: string;
            prevButton: string;
        };
        false: {};
    };
    showMonthAndYearPickers: {
        true: {
            base: string;
            header: string;
        };
        false: {};
    };
    showShadow: {
        true: {
            cellButton: string;
        };
        false: {
            cellButton: string;
        };
    };
    disableAnimation: {
        true: {
            cellButton: string;
        };
        false: {
            headerWrapper: string[];
            grid: string;
            cellButton: string[];
            pickerWrapper: string;
            pickerItem: string;
        };
    };
    isRTL: {
        true: {
            nextButton: string;
            prevButton: string;
        };
        false: {};
    };
}, {
    base: string[];
    prevButton: string[];
    nextButton: string[];
    headerWrapper: string[];
    header: string;
    title: string;
    content: string;
    gridWrapper: string;
    grid: string;
    gridHeader: string;
    gridHeaderRow: string;
    gridHeaderCell: string;
    gridBody: string;
    gridBodyRow: string;
    cell: string;
    cellButton: string[];
    pickerWrapper: string;
    pickerMonthList: string;
    pickerYearList: string;
    pickerHighlight: string;
    pickerItem: string[];
    helperWrapper: string;
    errorMessage: string;
}, undefined, unknown, unknown, undefined>>;
type CalendarReturnType = ReturnType<typeof calendar>;
type CalendarVariantProps = VariantProps<typeof calendar>;
type CalendarSlots = keyof ReturnType<typeof calendar>;

export { type CalendarReturnType, type CalendarSlots, type CalendarVariantProps, calendar };

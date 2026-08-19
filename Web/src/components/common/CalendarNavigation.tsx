import type { ReactNode } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';

function CalendarNavigation({
  currentMonth,
  startYear,
  endYear,
  monthNames,
  onChange,
  children
}: {
  currentMonth: Date;
  startYear: number;
  endYear: number;
  monthNames: string[];
  onChange: (month: Date) => void;
  children?: ReactNode;
}) {
  const changeMonth = (increment: number): void => {
    onChange(new Date(currentMonth.getFullYear(), currentMonth.getMonth() + increment, 1));
  };

  const handleMonthSelect = (option: string): void => {
    onChange(new Date(currentMonth.getFullYear(), Number(option), 1));
  };

  const handleYearSelect = (option: string): void => {
    onChange(new Date(Number(option), currentMonth.getMonth(), 1));
  };

  const yearOptions: DropdownOption[] = Array.from(
    { length: endYear - startYear + 1 },
    (_, index) => {
      const year = startYear + index;
      return { value: String(year), label: String(year) };
    }
  );
  const monthOptions: DropdownOption[] = monthNames.map((month, index) => ({
    value: String(index),
    label: month
  }));

  return (
    // One cluster, one gap. The row used to be justify-between with the two selects boxed in
    // their own gap-2 wrapper, which spread the arrows to the container edges and left four
    // different gaps in the same row - 272px of void either side of the selects at desktop
    // width. Every control is a direct flex item now, so the single gap-2 is the only spacing
    // in the row and the cluster sits at its own width instead of being stretched across one.
    // Centred, so the leftover in a track wider than the cluster is split rather than dumped
    // on one side: it fills the Events card at phone width and sits symmetrically in the
    // 314px picker popovers, which are the two callers with nothing in the trailing slot.
    <div className="mb-4 flex flex-wrap items-center justify-center gap-2">
      {/* md is the shared 40px control height, and below the phone breakpoint the dropdown
          takes a 44px touch floor, so the icon-only arrows are squares at whichever of the two
          is current. */}
      <Button
        variant="filled"
        color="gray"
        size="md"
        className="btn-icon-square max-sm:w-11 max-sm:h-11"
        onClick={() => changeMonth(-1)}
      >
        <ChevronLeft className="w-5 h-5" />
      </Button>

      {/* No width class: the trigger is a flex item now, so each select takes its own label
          plus its padding. The month names are all three letters on a phone and the years are
          all four digits, so the widths are stable without being pinned. */}
      <EnhancedDropdown
        options={monthOptions}
        value={String(currentMonth.getMonth())}
        onChange={handleMonthSelect}
        variant="button"
        size="md"
        maxHeight="200px"
        dropdownWidth="w-40"
      />

      <EnhancedDropdown
        options={yearOptions}
        value={String(currentMonth.getFullYear())}
        onChange={handleYearSelect}
        variant="button"
        size="md"
        alignRight
        maxHeight="200px"
        dropdownWidth="w-28"
      />

      <Button
        variant="filled"
        color="gray"
        size="md"
        className="btn-icon-square max-sm:w-11 max-sm:h-11"
        onClick={() => changeMonth(1)}
      >
        <ChevronRight className="w-5 h-5" />
      </Button>

      {/* Optional trailing slot, unwrapped so its controls join the row's own gap rather than
          carrying a second one of their own. */}
      {children}
    </div>
  );
}

export default CalendarNavigation;

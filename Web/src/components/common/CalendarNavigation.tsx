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
    // Two groups, not six loose controls. Stepping a month is one job, so the arrows and the
    // two selects sit in a tight cluster and read as one control; the caller's own actions are
    // a separate job and get a generous gap and the trailing edge. The wide gap is what makes
    // the two groups legible, so the inner gap stays deliberately smaller than it.
    //
    // Six controls cannot share one line on a phone: at a 298px track their own widths total
    // 309px before any gap at all. So the actions wrap to a second line by design and are
    // right-aligned there by the auto margin, rather than a lone control being orphaned in the
    // centre of a row that ran out of room.
    <div className="mb-4 flex flex-wrap items-center gap-x-2 gap-y-2 sm:gap-x-4">
      <div className="calendar-nav flex items-center gap-1 sm:gap-1.5">
        {/* md is the shared 40px control height, and below the phone breakpoint the dropdown
            takes a 44px touch floor, so the icon-only arrows are squares at whichever of the
            two is current. */}
        <Button
          variant="filled"
          color="secondary"
          size="md"
          className="btn-icon-square max-sm:w-11 max-sm:h-11"
          onClick={() => changeMonth(-1)}
        >
          <ChevronLeft className="w-5 h-5" />
        </Button>

        {/* Both selects are pinned. Letting them size to their label moves every control after
            them whenever the month changes, because proportional type gives Sep, May and Jul
            three different widths, and monthNames is localized so a locale can be wider still.
            The widths hold the longest label each list can produce, so the arrows never move. */}
        <EnhancedDropdown
          options={monthOptions}
          value={String(currentMonth.getMonth())}
          onChange={handleMonthSelect}
          variant="button"
          size="md"
          maxHeight="200px"
          dropdownWidth="w-40"
          className="w-[68px] sm:w-[120px]"
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
          className="w-[72px] sm:w-[92px]"
        />

        <Button
          variant="filled"
          color="secondary"
          size="md"
          className="btn-icon-square max-sm:w-11 max-sm:h-11"
          onClick={() => changeMonth(1)}
        >
          <ChevronRight className="w-5 h-5" />
        </Button>
      </div>

      {/* Only the two Events callers pass anything here. The auto margin is inside the guard so
          the two date pickers, which pass nothing, keep a cluster that is not pushed anywhere. */}
      {children ? <div className="ml-auto flex items-center gap-2">{children}</div> : null}
    </div>
  );
}

export default CalendarNavigation;

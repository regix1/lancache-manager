import React from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, type LucideIcon } from 'lucide-react';
import { formatCount } from '@utils/formatters';
import { themeColorVar, type ColorToken } from '@utils/eventColors';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';

/** Lucide icons or brand SVG components (SteamIcon, EpicIcon, …) that accept size/className/style. */
type AccordionIcon =
  | LucideIcon
  | React.ComponentType<{ size?: number; className?: string; style?: React.CSSProperties }>;

interface AccordionSectionProps {
  title: string;
  /**
   * Shown instead of `title` below 640px, where the title line only has room for
   * roughly fifteen characters before the accessory drops onto a line of its own
   * and pushes the actions row away from the collapse arrow. Omit it and the full
   * title renders at every width.
   */
  shortTitle?: string;
  titleAccessory?: React.ReactNode;
  count?: number;
  icon?: AccordionIcon;
  /**
   * Colour token for the icon and its box. The header tints the box with the token's `-subtle`
   * and `-muted` tiers, so the token has to be one the theme emits those tiers for - the closed
   * `ColorToken` union is what guarantees that.
   */
  iconColor?: ColorToken;
  children: React.ReactNode;
  isExpanded: boolean;
  onToggle: () => void;
  badge?: React.ReactNode;
  /**
   * 'card' (default) matches the app-wide card shell. 'well' renders a stable
   * recessed-well fill/border in both expanded and collapsed states, for
   * accordions nested inside another AccordionSection — avoids a card-in-card look.
   */
  surface?: 'card' | 'well';
}

export const AccordionSection: React.FC<AccordionSectionProps> = ({
  title,
  shortTitle,
  titleAccessory,
  count,
  icon: Icon,
  iconColor = '--theme-accent',
  children,
  isExpanded,
  onToggle,
  badge,
  surface = 'card'
}) => {
  const { t } = useTranslation();

  const handleKeyDown = (e: React.KeyboardEvent) => {
    const target = e.target as HTMLElement;
    if (
      target.closest(
        'button, input, select, a, [role="listbox"], [role="combobox"], .ed-trigger, .ed-dropdown'
      )
    ) {
      return;
    }

    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      onToggle();
    }
  };

  const handleHeaderClick = (e: React.MouseEvent) => {
    // Don't toggle if clicking on an interactive element inside the header
    const target = e.target as HTMLElement;
    if (
      target.closest(
        'button, input, select, a, [role="listbox"], [role="combobox"], .ed-trigger, .ed-dropdown'
      )
    ) {
      return;
    }
    onToggle();
  };

  const handleTouchEnd = (e: React.TouchEvent) => {
    // Don't toggle if touching an interactive element inside the header
    const target = e.target as HTMLElement;
    if (
      target.closest(
        'button, input, select, a, [role="listbox"], [role="combobox"], .ed-trigger, .ed-dropdown'
      )
    ) {
      return;
    }
  };

  const chevronButton = (
    <button
      type="button"
      onClick={(e) => {
        e.stopPropagation();
        onToggle();
      }}
      className={`flex items-center justify-center ${
        surface === 'well' ? 'w-7 h-7' : 'w-10 h-10'
      } themed-button-radius transition duration-300 flex-shrink-0 ${
        isExpanded ? 'bg-[var(--theme-accent-subtle)]' : 'bg-transparent hover:bg-themed-tertiary'
      }`}
      aria-label={isExpanded ? t('ui.accordion.collapseSection') : t('ui.accordion.expandSection')}
    >
      <ChevronDown
        className={`${surface === 'well' ? 'w-4 h-4' : 'w-5 h-5'} transition duration-300 ease-out ${
          isExpanded ? 'rotate-180 text-themed-accent' : 'rotate-0 text-themed-muted'
        }`}
      />
    </button>
  );

  return (
    <div
      className={`group themed-border-radius overflow-hidden transition duration-300 border ${
        surface === 'well'
          ? 'bg-transparent border-themed-well'
          : isExpanded
            ? 'bg-themed-card border-themed-primary shadow-[0_4px_16px_rgba(0,0,0,0.2),0_1px_4px_rgba(0,0,0,0.12)]'
            : 'bg-[var(--theme-card-bg-emphasis)] border-themed-secondary shadow-[0_1px_3px_rgba(0,0,0,0.1)]'
      }`}
    >
      {/* Header - using div with role="button" to allow nested interactive elements */}
      <div
        role="button"
        tabIndex={0}
        onClick={handleHeaderClick}
        onKeyDown={handleKeyDown}
        onTouchEnd={handleTouchEnd}
        className="w-full px-4 py-3 flex flex-wrap items-center gap-x-2 gap-y-3 sm:gap-x-3 text-left transition duration-200 group/header bg-transparent cursor-pointer"
      >
        <div className="flex items-center gap-3 min-w-0 flex-1 order-1">
          {/* Icon with animated background */}
          {Icon && (
            <div
              className={`icon-box icon-box--sm transition duration-300 ${
                isExpanded ? 'scale-105' : 'scale-100'
              }`}
              style={{
                backgroundColor: themeColorVar(iconColor, 'subtle'),
                boxShadow: isExpanded ? `0 2px 8px ${themeColorVar(iconColor, 'muted')}` : 'none'
              }}
            >
              <Icon
                className={`w-4 h-4 flex-shrink-0 transition-transform duration-300 ${
                  isExpanded ? 'scale-110' : 'scale-100'
                }`}
                style={{ color: themeColorVar(iconColor) }}
              />
            </div>
          )}

          <div className="flex flex-col gap-0.5 min-w-0 flex-1">
            <div className="accordion-title-line flex items-center gap-2 min-w-0 w-fit max-w-full">
              {/* Title — wraps to two lines before ellipsizing so narrow screens
                  keep the meaningful trailing words instead of cutting them off */}
              <span
                className={`font-semibold transition-colors duration-200 min-w-0 line-clamp-2 ${
                  isExpanded ? 'text-themed-primary' : 'text-themed-secondary'
                }`}
              >
                {shortTitle ? (
                  <>
                    {/* Only one of the two is ever laid out, and `hidden` is
                        display:none, which also drops the other from the
                        accessibility tree — so the title is announced once. */}
                    <span className="sm:hidden">{shortTitle}</span>
                    <span className="hidden sm:inline">{title}</span>
                  </>
                ) : (
                  title
                )}
              </span>

              {titleAccessory && (
                <span className="inline-flex flex-shrink-0 items-center">{titleAccessory}</span>
              )}

              {/* Count Badge */}
              {count !== undefined && (
                <span
                  className={`themed-badge badge-count font-semibold transition duration-300 flex-shrink-0 ${
                    isExpanded ? 'scale-105' : 'scale-100 bg-themed-tertiary text-themed-muted'
                  }`}
                  style={
                    isExpanded
                      ? {
                          backgroundColor: themeColorVar(iconColor, 'muted'),
                          color: themeColorVar(iconColor)
                        }
                      : undefined
                  }
                >
                  {formatCount(count)}
                </span>
              )}

              {/* Kebab / chips sit with the title, not against the chevron. The title
                  cluster is width-fit so flex-1 on the parent does not shove these
                  to the far edge. */}
              {badge && (
                <span className="section-header-actions inline-flex flex-wrap items-center flex-shrink-0">
                  {badge}
                </span>
              )}
            </div>
          </div>
        </div>

        {/* Chevron stays on the far right at every width. */}
        <span className="flex flex-shrink-0 order-2 self-start sm:self-auto">{chevronButton}</span>
      </div>

      {/* Content with real height animation; children unmount once collapsed */}
      <CollapsibleRegion
        open={isExpanded}
        contentClassName="px-4 pb-4 pt-3 border-t border-themed-secondary"
      >
        {children}
      </CollapsibleRegion>
    </div>
  );
};

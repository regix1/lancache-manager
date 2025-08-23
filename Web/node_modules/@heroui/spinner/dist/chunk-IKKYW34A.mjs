"use client";

// src/use-spinner.ts
import { mapPropsVariants } from "@heroui/system-rsc";
import { spinner } from "@heroui/theme";
import { clsx, objectToDeps } from "@heroui/shared-utils";
import { useMemo, useCallback } from "react";
import { useProviderContext } from "@heroui/system";
function useSpinner(originalProps) {
  var _a, _b;
  const [props, variantProps] = mapPropsVariants(originalProps, spinner.variantKeys);
  const globalContext = useProviderContext();
  const variant = (_b = (_a = originalProps == null ? void 0 : originalProps.variant) != null ? _a : globalContext == null ? void 0 : globalContext.spinnerVariant) != null ? _b : "default";
  const { children, className, classNames, label: labelProp, ...otherProps } = props;
  const slots = useMemo(() => spinner({ ...variantProps }), [objectToDeps(variantProps)]);
  const baseStyles = clsx(classNames == null ? void 0 : classNames.base, className);
  const label = labelProp || children;
  const ariaLabel = useMemo(() => {
    if (label && typeof label === "string") {
      return label;
    }
    return !otherProps["aria-label"] ? "Loading" : "";
  }, [children, label, otherProps["aria-label"]]);
  const getSpinnerProps = useCallback(
    () => ({
      "aria-label": ariaLabel,
      className: slots.base({
        class: baseStyles
      }),
      ...otherProps
    }),
    [ariaLabel, slots, baseStyles, otherProps]
  );
  return { label, slots, classNames, variant, getSpinnerProps };
}

export {
  useSpinner
};

"use client";
import {
  useNavbarContext
} from "./chunk-IGERPFKH.mjs";

// src/navbar-brand.tsx
import { forwardRef } from "@heroui/system";
import { useDOMRef } from "@heroui/react-utils";
import { clsx } from "@heroui/shared-utils";
import { jsx } from "react/jsx-runtime";
var NavbarBrand = forwardRef((props, ref) => {
  var _a;
  const { as, className, children, ...otherProps } = props;
  const Component = as || "div";
  const domRef = useDOMRef(ref);
  const { slots, classNames } = useNavbarContext();
  const styles = clsx(classNames == null ? void 0 : classNames.brand, className);
  return /* @__PURE__ */ jsx(Component, { ref: domRef, className: (_a = slots.brand) == null ? void 0 : _a.call(slots, { class: styles }), ...otherProps, children });
});
NavbarBrand.displayName = "HeroUI.NavbarBrand";
var navbar_brand_default = NavbarBrand;

export {
  navbar_brand_default
};

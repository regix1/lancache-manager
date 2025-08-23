"use client";
import {
  useNavbarContext
} from "./chunk-IGERPFKH.mjs";

// src/navbar-menu-item.tsx
import { forwardRef } from "@heroui/system";
import { useDOMRef } from "@heroui/react-utils";
import { clsx, dataAttr } from "@heroui/shared-utils";
import { jsx } from "react/jsx-runtime";
var NavbarMenuItem = forwardRef((props, ref) => {
  var _a;
  const { className, children, isActive, ...otherProps } = props;
  const domRef = useDOMRef(ref);
  const { slots, isMenuOpen, classNames } = useNavbarContext();
  const styles = clsx(classNames == null ? void 0 : classNames.menuItem, className);
  return /* @__PURE__ */ jsx(
    "li",
    {
      ref: domRef,
      className: (_a = slots.menuItem) == null ? void 0 : _a.call(slots, { class: styles }),
      "data-active": dataAttr(isActive),
      "data-open": dataAttr(isMenuOpen),
      ...otherProps,
      children
    }
  );
});
NavbarMenuItem.displayName = "HeroUI.NavbarMenuItem";
var navbar_menu_item_default = NavbarMenuItem;

export {
  navbar_menu_item_default
};

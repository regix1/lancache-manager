"use client";
import {
  useDropdownContext
} from "./chunk-BV7BCS3N.mjs";

// src/dropdown-menu.tsx
import { PopoverContent } from "@heroui/popover";
import { FocusScope } from "@react-aria/focus";
import { forwardRef } from "@heroui/system";
import { Menu } from "@heroui/menu";
import { jsx } from "react/jsx-runtime";
var DropdownMenu = forwardRef(function DropdownMenu2(props, ref) {
  const { getMenuProps } = useDropdownContext();
  return /* @__PURE__ */ jsx(PopoverContent, { children: /* @__PURE__ */ jsx(FocusScope, { contain: true, restoreFocus: true, children: /* @__PURE__ */ jsx(Menu, { ...getMenuProps(props, ref) }) }) });
});
var dropdown_menu_default = DropdownMenu;

export {
  dropdown_menu_default
};

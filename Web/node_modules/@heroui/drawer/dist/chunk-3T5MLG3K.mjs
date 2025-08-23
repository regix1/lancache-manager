"use client";
import {
  useDrawer
} from "./chunk-DULHZX7T.mjs";

// src/drawer.tsx
import { forwardRef } from "@heroui/system";
import { Modal } from "@heroui/modal";
import { jsx } from "react/jsx-runtime";
var Drawer = forwardRef(({ children, ...props }, ref) => {
  const { domRef, getModalProps } = useDrawer({ ...props, ref });
  return /* @__PURE__ */ jsx(Modal, { ref: domRef, ...getModalProps(), children });
});
Drawer.displayName = "HeroUI.Drawer";
var drawer_default = Drawer;

export {
  drawer_default
};

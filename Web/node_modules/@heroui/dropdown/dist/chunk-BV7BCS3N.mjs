"use client";

// src/dropdown-context.ts
import { createContext } from "@heroui/react-utils";
var [DropdownProvider, useDropdownContext] = createContext({
  name: "DropdownContext",
  errorMessage: "useDropdownContext: `context` is undefined. Seems you forgot to wrap all popover components within `<Dropdown />`"
});

export {
  DropdownProvider,
  useDropdownContext
};

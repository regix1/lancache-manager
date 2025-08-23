"use client";

// src/navbar-context.ts
import { createContext } from "@heroui/react-utils";
var [NavbarProvider, useNavbarContext] = createContext({
  name: "NavbarContext",
  strict: true,
  errorMessage: "useNavbarContext: `context` is undefined. Seems you forgot to wrap component within <Navbar />"
});

export {
  NavbarProvider,
  useNavbarContext
};

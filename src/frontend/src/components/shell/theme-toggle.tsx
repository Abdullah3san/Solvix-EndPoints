"use client";

import * as React from "react";
import { useTheme } from "next-themes";
import { Check, Monitor, Moon, Sun } from "lucide-react";
import { Menu, MenuItem, MenuLabel, MenuPanel, MenuTrigger } from "@/components/ui/menu";

const subscribeNoop = () => () => {};

const options = [
  { value: "light", label: "Light", icon: Sun },
  { value: "dark", label: "Dark", icon: Moon },
  { value: "system", label: "System", icon: Monitor },
] as const;

export function ThemeToggle() {
  const { theme, resolvedTheme, setTheme } = useTheme();
  // next-themes only knows the real theme on the client; render a neutral icon during SSR.
  const mounted = React.useSyncExternalStore(subscribeNoop, () => true, () => false);

  const CurrentIcon = mounted && resolvedTheme === "dark" ? Moon : Sun;

  return (
    <Menu>
      <MenuTrigger className="h-9 w-9 justify-center text-muted-foreground hover:bg-muted hover:text-foreground" aria-label="Select theme" title="Theme">
        <CurrentIcon className="h-[18px] w-[18px]" />
      </MenuTrigger>
      <MenuPanel label="Theme">
        <MenuLabel>Theme</MenuLabel>
        {options.map((option) => (
          <MenuItem key={option.value} onSelect={() => setTheme(option.value)} active={mounted && theme === option.value}>
            <option.icon className="h-4 w-4" />
            <span className="flex-1">{option.label}</span>
            {mounted && theme === option.value ? <Check className="h-4 w-4" /> : null}
          </MenuItem>
        ))}
      </MenuPanel>
    </Menu>
  );
}

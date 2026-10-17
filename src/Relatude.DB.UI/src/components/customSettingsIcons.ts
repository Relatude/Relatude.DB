import type { ComponentType } from "react";
import {
  IconBell,
  IconBuildingStore,
  IconCloud,
  IconCreditCard,
  IconDatabase,
  IconFileText,
  IconFolders,
  IconMail,
  IconMessage,
  IconPalette,
  IconServer,
  IconSettings,
  IconShieldLock,
  IconShoppingCart,
  IconSparkles,
  IconTruck,
  IconUsers,
  IconWorld,
} from "@tabler/icons-react";

type Icon = ComponentType<{ size?: number; stroke?: number }>;

/**
 * The glyphs a set of custom settings can ask for by name (ICustomSettingsDefinition.Icon on the server),
 * for its entry in the rail and its section on the page. The names a settings section of the server's own
 * uses work too; anything else gets the plain settings icon rather than a hole.
 */
export const customSettingsIcons: Record<string, Icon> = {
  settings: IconSettings,
  server: IconServer,
  database: IconDatabase,
  security: IconShieldLock,
  storage: IconFolders,
  content: IconFileText,
  messaging: IconMessage,
  services: IconCloud,
  search: IconSparkles,
  shop: IconShoppingCart,
  store: IconBuildingStore,
  payment: IconCreditCard,
  shipping: IconTruck,
  mail: IconMail,
  notifications: IconBell,
  users: IconUsers,
  site: IconWorld,
  theme: IconPalette,
};

export function customSettingsIcon(name: string): Icon {
  return customSettingsIcons[name] ?? IconSettings;
}

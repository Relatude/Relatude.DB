// The Databases section: every database on this server, and the three things decided about a
// database rather than inside one - whether it runs, which one is the default, and adding another.
// See UIDatabases.cs; opening and closing are the same store-open / store-close commands the rest of
// the UI uses (server/storage.ts).

import { send } from "./channel";

export interface DatabaseRow {
  id: string;
  name: string;
  description?: string | null;
  state: string;
  isDefault: boolean;
  autoOpen: boolean;
  /** Only while the database is open; a closed one has nothing to count. */
  nodeCount?: number | null;
  relationCount?: number | null;
  /** Where its files live: the folder for a local disk, otherwise the provider. */
  storage: string;
  /** How many datamodel sources are switched on - none means the model is still to be decided. */
  modelSources: number;
  startupError?: string | null;
}

export interface DatabaseList {
  /** Configuration decides the default database, so the button here would not survive a restart. */
  defaultLocked: boolean;
  configSection?: string | null;
  settingsFile: string;
  databases: DatabaseRow[];
}

export function fetchDatabases(): Promise<DatabaseList> {
  return send<DatabaseList>("databases");
}

/** Which database an application gets when it asks for none in particular. Opens nothing. */
export function setDefaultDatabase(storeId: string): Promise<DatabaseList> {
  return send<DatabaseList>("database-set-default", { storeId });
}

export interface CreatedDatabase {
  storeId: string;
  folder: string;
  /** The list as it now stands, so the page does not have to ask again. */
  list: DatabaseList;
}

/** A new, empty database in a folder of its own. Created closed and with no datamodel sources. */
export function createDatabase(name: string, autoOpen: boolean): Promise<CreatedDatabase> {
  return send<CreatedDatabase>("database-create", { name, autoOpen });
}

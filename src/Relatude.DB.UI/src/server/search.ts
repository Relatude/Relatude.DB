// The global search of the top bar: one word, everything the UI can open (see UISearch.cs).

import { send } from "./channel";

/** A node type whose name matches. */
export interface TypeHit {
  id: string;
  name: string;
  /** the namespaced name, which is what tells two types of the same name apart */
  fullName: string;
  kind: string;
  isInterface: boolean;
  isBase: boolean;
}

/** A property or relation whose name matches, under the type that declares it. */
export interface PropertyHit {
  id: string;
  name: string;
  typeId: string;
  typeName: string;
  /** the property type, or "Relation (Name)" for a relation */
  kind: string;
}

/** A setting whose label, path, group or explanation mentions the word. */
export interface SettingHit {
  scope: "server" | "database";
  /** the database whose settings these are; absent for the server's own */
  storeId: string | null;
  sectionId: string;
  sectionTitle: string;
  groupId: string;
  groupTitle: string;
  path: string;
  label: string;
  help: string;
}

/** A node the text index found. */
export interface NodeHit {
  id: string;
  name: string;
  typeName: string;
  typeId: string;
}

export interface SearchResults {
  /** the text these results answer, so a late answer to an old keystroke can be dropped */
  text: string;
  types: TypeHit[];
  properties: PropertyHit[];
  settings: SettingHit[];
  nodes: NodeHit[];
}

/** Below this the server answers nothing: a one-letter prefix matches most of a database. */
export const minSearchLength = 2;

export function globalSearch(storeId: string | null, text: string): Promise<SearchResults> {
  return send("global-search", { storeId, text });
}

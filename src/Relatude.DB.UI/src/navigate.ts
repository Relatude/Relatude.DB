// Going from one page to a particular thing on another one.
//
// The pages do not know about each other, and a page that wants to send someone to the model editor
// or to a query on a type should not have to reach into them. A request is left here instead: the
// shell picks it up and switches the section, and the page that owns the target picks it up and
// opens what was asked for. One request at a time, cleared when it is taken - a hand-off, not a route.

import { useSyncExternalStore } from "react";

/** Where in the data model to open: a type, and one of its properties or relations when named. */
export interface DatamodelTarget {
  /** the type that declares the thing; the editor opens on it when nothing more is given */
  typeId: string;
  propertyId?: string;
  relationId?: string;
}

let pending: DatamodelTarget | null = null;
let serial = 0;
const listeners = new Set<() => void>();

function notify() {
  serial++;
  for (const listener of listeners) listener();
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** Asks for the data model page, opened on this. */
export function openInDatamodel(target: DatamodelTarget): void {
  pending = target;
  notify();
}

/** The request, without taking it: what the shell watches to know it has to switch section. */
export function peekDatamodelTarget(): DatamodelTarget | null {
  return pending;
}

/** The request, taken: the page that opens it consumes it, so a later remount does not repeat it. */
export function takeDatamodelTarget(): DatamodelTarget | null {
  const target = pending;
  if (target) {
    pending = null;
    // no notify: taking it is not a change anyone else has to react to, and notifying from inside a
    // render or an effect would loop
  }
  return target;
}

/** Where in the query page to open: a type, as a new query on it. */
export interface QueryTarget {
  typeId: string;
}

let pendingQuery: QueryTarget | null = null;

/** Asks for the query page, with a new query on this type. */
export function openInQuery(target: QueryTarget): void {
  pendingQuery = target;
  notify();
}

/** The request, without taking it: what the shell watches to know it has to switch section. */
export function peekQueryTarget(): QueryTarget | null {
  return pendingQuery;
}

/** The request, taken: the query page consumes it, so a later remount does not repeat it. */
export function takeQueryTarget(): QueryTarget | null {
  const target = pendingQuery;
  if (target) pendingQuery = null;
  return target;
}

/** Re-renders on every request, so a component can act on one it did not make. */
export function useNavigationRequest(): number {
  return useSyncExternalStore(subscribe, () => serial, () => serial);
}

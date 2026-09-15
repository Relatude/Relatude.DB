// Whether the panel being rendered right now has the whole page (see PanelGrid).
//
// The button that maximizes a panel belongs to the grid, not to the panel, but a panel that has
// more to say once it is given the room has to know it got it: the memory list is one bar per
// budget in a half-page cell, and every figure behind each of them when it is the page. A context
// rather than a prop, because the panels are handed to the grid as elements and nothing in between
// would carry the flag; it lives in its own file so the grid keeps exporting only its component.

import { createContext, useContext } from "react";

export const PanelMaximizedContext = createContext(false);

/** True while this panel is the maximized one. False everywhere else, including outside a grid. */
export function usePanelMaximized(): boolean {
  return useContext(PanelMaximizedContext);
}

import { createRoot } from "react-dom/client";
import "../app.css";
import { applyTheme, getInitialTheme } from "../theme";
import { ExplorerPage } from "./ExplorerPage";

// The entry of the explorer page served on a GraphQL endpoint's url (explorer.html, vite.explorer.config.ts).
// The theme is set before the first paint, so a dark page does not flash white.
applyTheme(getInitialTheme());
createRoot(document.getElementById("app")!).render(<ExplorerPage />);

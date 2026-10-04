// Links that leave the UI for the Relatude.DB site (docs/ in the repository, published on db.relatude.com).

/** The site's front page */
export const siteUrl = "https://db.relatude.com";

/** The manual, at a chapter when an anchor is given (anchors are the manual's heading ids, e.g. "33-graphql-endpoints") */
export function manualUrl(anchor?: string): string {
  return siteUrl + "/manual.html" + (anchor ? "#" + anchor : "");
}

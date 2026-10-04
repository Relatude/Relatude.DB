import { useEffect, useState } from "react";
import { DialogTools } from "../components/DialogTools";
import { CopyText } from "../components/CopyText";
import { formatCount, formatDateTime } from "../format";
import type { FacetNode, FacetNodeProperty, FacetSource } from "./facetSource";

// What a click on a card of the facet page opens: the node the card stands for, as the endpoint shows it - the type
// it is seen as, and every property behind its type's fields under the datamodel's names. The server decides what
// is in it ("?facets=node", NodeServer/GraphQL/GraphQLFacetSearch.cs); this only lays it out.

export function NodeDialog({ source, nodeId, onClose }: { source: FacetSource; nodeId: number; onClose: () => void }) {
  const [node, setNode] = useState<FacetNode | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    setNode(null);
    setError(null);
    source.node(nodeId, controller.signal).then(
      (n) => !controller.signal.aborted && setNode(n),
      (e) => !controller.signal.aborted && setError(e instanceof Error ? e.message : String(e)),
    );
    return () => controller.abort();
  }, [source, nodeId]);

  // Escape closes it, as it closes every dialog
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      e.preventDefault();
      onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="dialog dialog-wide fp-node" role="dialog" aria-label={node?.name ?? "Node"}>
        <h3>
          <span className="fp-node-title">{node ? node.name || "(no name)" : error ? "Not found" : "Loading…"}</span>
          <DialogTools onClose={onClose} />
        </h3>
        {node && (
          <div className="fp-node-meta muted">
            <span className="fp-node-type">{node.type}</span>
            <span className="mono">{node.id}</span>
            <CopyText text={node.id} title="Copy the id" small />
          </div>
        )}
        {error && <div className="query-error">{error}</div>}
        {node && (
          <div className="fp-node-values">
            {node.properties.length === 0 && <div className="muted fp-node-none">The endpoint shows no properties of this node.</div>}
            {node.properties.map((p) => (
              <div key={p.id} className="fp-node-row">
                <span className="fp-node-name" title={p.kind}>
                  {p.name}
                  <span className="muted fp-node-kind">{p.kind}</span>
                </span>
                <PropertyValue property={p} />
              </div>
            ))}
          </div>
        )}
        <div className="dialog-row">
          {node && (
            <span className="muted fp-node-dates">
              Created {formatDateTime(node.createdUtc)} · changed {formatDateTime(node.changedUtc)}
            </span>
          )}
          <span className="header-spacer" />
          <button className="action-button" onClick={onClose}>
            Close
          </button>
        </div>
      </div>
    </div>
  );
}

function PropertyValue({ property: p }: { property: FacetNodeProperty }) {
  if (p.nodes) {
    const count = p.count ?? p.nodes.length;
    if (count === 0) return <span className="fp-node-value muted">none</span>;
    const more = count - p.nodes.length;
    return (
      <span className="fp-node-value fp-node-nodes">
        {p.nodes.map((name, i) => (
          <span key={i} className="fp-node-chip">
            {name || "(no name)"}
          </span>
        ))}
        {more > 0 && <span className="muted">{p.nodes.length > 0 ? `and ${formatCount(more)} more` : `${formatCount(more)} not shown`}</span>}
      </span>
    );
  }
  if (p.value === null) return <span className="fp-node-value muted">could not be read</span>;
  if (p.value === "") return <span className="fp-node-value muted">—</span>;
  return <span className="fp-node-value">{p.value}</span>;
}

import { useState } from "react";
import { IconBook, IconExternalLink, IconPlayerPlay, IconPointer } from "@tabler/icons-react";
import { highlight } from "../code/highlight";
import type { GuideExample } from "../server/graphql";
import { manualUrl } from "../siteLinks";

// The Guide panel: what the explorer can do and what the endpoint can answer, topic by topic. Each GraphQL
// topic carries an example written by the server for this endpoint (GraphQLGuide.cs), with its names and,
// where the database has them, real ids and values - Run opens it in a tab and runs it. The explorer's own
// features come with a button that shows them.

export type GuideAction = "builder" | "complete" | "docs" | "mistake" | "table" | "code" | "history";

interface Topic {
  id: string;
  title: string;
  text: string;
  /** said only in the admin UI, where the explorer sits inside the endpoint's editor */
  adminText?: string;
  /** for the explorer's own features: what the button does */
  action?: { kind: GuideAction; label: string };
  warn?: string;
}

interface Section {
  title: string;
  intro?: string;
  /** the intro on the endpoint's own page, when it differs */
  publicIntro?: string;
  topics: Topic[];
}

export const guideSections: Section[] = [
  {
    title: "Getting around",
    intro: "The explorer runs queries against this endpoint as it is being edited here, saved or not. Everything below works on this endpoint's own types.",
    publicIntro: "Everything below is written for this endpoint, with its own types and names; Run sends an example to it and shows the answer.",
    topics: [
      {
        id: "builder",
        title: "Build a query by ticking fields",
        text: "Build lists what the endpoint offers. Tick a field to add it to the query and untick it to take it out; a ticked field shows its arguments, each with an input that fits its type, and the fields of what it returns. The variable button next to a value passes it as a variable instead.",
        action: { kind: "builder", label: "Open the builder" },
      },
      {
        id: "complete",
        title: "Type with completion",
        text: "Ctrl+Space lists what can be typed at the caret: fields, arguments, enum values, types after on, variables after $ and directives after @. The list also opens while a name is typed. Enter or Tab puts the entry in, Esc closes the list.",
        action: { kind: "complete", label: "Try it in a new tab" },
      },
      {
        id: "docs",
        title: "Read the schema",
        text: "Docs describes every type: its fields and their arguments, the datamodel type behind it, what it implements and where it is used. Type names are links, the search finds types and fields, and + on a root field adds it to the query.",
        action: { kind: "docs", label: "Open the docs" },
      },
      {
        id: "mistake",
        title: "Mistakes are marked as you type",
        text: "The query is checked against the schema on every keystroke. A red line number marks a problem and its tooltip says what is wrong; a misspelled name gets the name that was probably meant. The line above the editor shows the first problem - click it to go there.",
        action: { kind: "mistake", label: "Show a mistake" },
      },
      {
        id: "table",
        title: "Results as JSON or as a table",
        text: "Run with the button or Ctrl+Enter. The answer shows as JSON or as a table of each list in it, with the time it took and its size. A node in the table opens in a new tab with all its fields and relations, so the graph can be walked from node to node.",
        action: { kind: "table", label: "Run an example as a table" },
      },
      {
        id: "code",
        title: "Code in your language",
        text: "Code writes the query as code: model types for its answer and its variables, and the code that connects to the endpoint and runs it - TypeScript, JavaScript, C#, Python, Java and Go, or a curl or PowerShell command. Whole schema gives the types of every node type instead, with a client to send any query.",
        action: { kind: "code", label: "Show the code" },
      },
      {
        id: "history",
        title: "Tabs, history and shortcuts",
        text: "Each tab keeps its query and variables, also after a reload, and every run is kept under History. With several operations in one tab, the one the caret is in runs (or pick it next to Run). Ctrl+Enter runs, Ctrl+Space completes and Shift+Alt+F lays the query out.",
        adminText: "The maximize button gives the explorer the whole window.",
        action: { kind: "history", label: "Open the history" },
      },
    ],
  },
  {
    title: "Reading data",
    topics: [
      {
        id: "first",
        title: "Your first query",
        text: "A list field returns a page: items holds the nodes and totalCount how many match in all. Only the fields picked under items are fetched.",
      },
      {
        id: "paging",
        title: "Paging",
        text: "page counts from 0 and pageSize sets how many items a page holds. In the answer, pageIndex and pageSize say what was returned and durationMs how long the store took.",
      },
      { id: "one", title: "One node by id", text: "Each type also has a field that fetches one node by its id, or null when there is none." },
      {
        id: "filter",
        title: "Filtering",
        text: "filter takes conditions on the type's properties: eq and ne, and for numbers and dates gt, gte, lt and lte. Conditions side by side must all hold.",
      },
      { id: "filter-range", title: "A range", text: "gte and lte together make a range. Here the values are written straight into the query rather than passed as variables." },
      { id: "filter-in", title: "One of several values", text: "in matches any of the values in a list, nin none of them." },
      {
        id: "filter-logic",
        title: "or, not and and",
        text: "or takes a list of conditions of which one must hold, not turns a condition around, and and takes a list that must all hold. They nest as deep as needed.",
      },
      { id: "enums", title: "Enums", text: "An enum property is filtered and returned by its values, which are written without quotes." },
      {
        id: "search",
        title: "Free-text search",
        text: "search runs the database's text index, so words match the way its search matches them, and the best matches come first unless orderBy says otherwise.",
      },
      { id: "order", title: "Ordering", text: "orderBy takes any property that can be sorted; descending turns the order around." },
      {
        id: "relations",
        title: "Following relations",
        text: "A relation is a field like any other: pick fields on the node at the other end, and follow its relations from there. top limits how many related nodes come back.",
      },
      { id: "relation-filter", title: "Filtering by a related node", text: "A relation in a filter takes the id of the node at the other end: eq for one, in for any of several." },
      { id: "ids", title: "Given ids", text: "ids limits a list to the nodes with these ids, fetched in one request." },
      { id: "views", title: "Views", text: "A view is a list defined by a query in the endpoint's settings. It takes the same arguments as the list of its type." },
      { id: "files", title: "Files", text: "A file property gives the file's name, size and content type, for images and videos their width and height, and the url the file is served on - or, with width, height, crop, format or quality, the url of a resized image made from it." },
      { id: "geo", title: "Positions", text: "A position property gives latitude and longitude." },
    ],
  },
  {
    title: "Shaping the request",
    topics: [
      {
        id: "variables",
        title: "Variables",
        text: "Values can travel beside the query instead of inside it: declare them after the operation's name as $name: Type and give their values as JSON under Variables. A default after = is used when no value is given.",
      },
      { id: "aliases", title: "Aliases", text: "name: field puts a field in the answer under another name, so one field can be asked for twice with different arguments." },
      { id: "fragments", title: "Fragments", text: "A fragment names a set of fields once, to be spread with ...Name wherever its type fits." },
      {
        id: "inline-fragments",
        title: "Subtypes",
        text: "When a list holds nodes of several types, … on Type { } picks what only that type has, and __typename tells which type each node is.",
      },
      { id: "directives", title: "@include and @skip", text: "@include(if: $flag) keeps a field only when the flag is true, and @skip(if: $flag) leaves it out when it is." },
      { id: "typename", title: "__typename", text: "Any object can give the name of its type, which tells subtypes apart in an answer." },
      {
        id: "multiple-roots",
        title: "Several fields in one request",
        text: "A query can ask for any number of root fields; they come back together in one answer, in one round trip.",
      },
      {
        id: "operation-name",
        title: "Several operations in one tab",
        text: "A document can hold several named operations, and a request names the one to run. Here the one the caret is in runs: click into the other one, or pick it next to Run, and run again.",
      },
    ],
  },
  {
    title: "The schema itself",
    topics: [
      { id: "introspection-type", title: "Introspection: one type", text: "__type(name:) describes a type: its fields, their types and descriptions." },
      {
        id: "introspection-schema",
        title: "Introspection: the whole schema",
        text: "__schema lists the root fields and every type. Code generators and other tools read the schema this way.",
      },
    ],
  },
  {
    title: "Changing data",
    intro: "Mutations change the database for real; the explorer runs them on this endpoint's definition as it is here.",
    publicIntro: "Mutations change the database for real.",
    topics: [
      {
        id: "create",
        title: "Creating a node",
        text: "createX takes an input with the values to set and returns the new node, selected like any other.",
        warn: "Running it adds a node to the database.",
      },
      { id: "update", title: "Updating a node", text: "updateX takes the node's id and an input with what to change.", warn: "Paste the id of a node you mean to change." },
      {
        id: "delete",
        title: "Deleting a node",
        text: "deleteX takes the node's id and answers true when it is gone.",
        warn: "Paste the id of a node you mean to delete; this cannot be undone.",
      },
    ],
  },
];

export function ExplorerGuide({
  examples,
  unavailable,
  onOpen,
  onAction,
  audience,
}: {
  audience: "admin" | "public";
  examples: GuideExample[];
  unavailable: { id: string; reason: string }[];
  onOpen: (topic: { id: string; title: string }, example: GuideExample, run: boolean) => void;
  onAction: (action: GuideAction) => void;
}) {
  const byId = new Map(examples.map((e) => [e.id, e]));
  // the reasons point to where the endpoint is edited; a visitor to its page has no such place
  const reasonFor = (reason: string) => (audience === "admin" ? reason : reason.replace(/\s*\(Settings\)/g, "").replace(/; add one under Views\./, "."));
  const why = new Map(unavailable.map((u) => [u.id, reasonFor(u.reason)]));
  // a reason shared by many topics (no types yet, mutations off…) is said once, at the top
  const counts = new Map<string, number>();
  for (const reason of why.values()) counts.set(reason, (counts.get(reason) ?? 0) + 1);
  const common = [...counts].filter(([, n]) => n >= 3).map(([reason]) => reason);
  return (
    <div className="gx-guide">
      <a className="gx-guide-manual" href={manualUrl("33-graphql-endpoints")} target="_blank" rel="noreferrer" title="The Relatude.DB manual's chapter on GraphQL endpoints - opens in a new tab">
        <IconBook size={14} stroke={1.8} />
        <span>
          The full manual: <b>GraphQL endpoints</b>
        </span>
        <IconExternalLink size={12} stroke={1.8} />
      </a>
      {common.length > 0 && (
        <div className="gx-guide-notice">
          {common.map((reason) => (
            <p key={reason}>
              {reason} <span className="muted">The guide leaves out the topics this rules out.</span>
            </p>
          ))}
        </div>
      )}
      {guideSections.map((section) => (
        <div key={section.title} className="gx-guide-section">
          {section.topics.some((t) => t.action || byId.has(t.id) || (why.has(t.id) && !common.includes(why.get(t.id)!))) && (
            <div className="gx-guide-section-head">{section.title}</div>
          )}
          {section.intro && section.topics.some((t) => t.action || byId.has(t.id)) && (
            <p className="gx-guide-intro muted">{audience === "public" ? (section.publicIntro ?? section.intro) : section.intro}</p>
          )}
          {section.topics.map((topic) => {
            const example = byId.get(topic.id);
            const reason = why.get(topic.id);
            if (!topic.action && !example && (!reason || common.includes(reason))) return null;
            return (
              <div key={topic.id} className={"gx-topic" + (reason && !example ? " unavailable" : "")} id={"gx-topic-" + topic.id}>
                <div className="gx-topic-title">{topic.title}</div>
                <p className="gx-topic-text">
                  {topic.text}
                  {audience === "admin" && topic.adminText ? " " + topic.adminText : ""}
                </p>
                {example?.note && <p className="gx-topic-note">{example.note}</p>}
                {reason && !example && <p className="gx-topic-note muted">Not on this endpoint: {reason}</p>}
                {example && <ExampleCode example={example} />}
                {topic.warn && example && <p className="gx-topic-warn">{topic.warn}</p>}
                <div className="gx-topic-actions">
                  {example && (
                    <>
                      <button className="action-button small primary" onClick={() => onOpen(topic, example, true)}>
                        <IconPlayerPlay size={13} stroke={1.8} /> Run
                      </button>
                      <button className="action-button small" onClick={() => onOpen(topic, example, false)}>
                        <IconExternalLink size={13} stroke={1.8} /> Open in a tab
                      </button>
                    </>
                  )}
                  {topic.action && (
                    <button className="action-button small" onClick={() => onAction(topic.action!.kind)}>
                      <IconPointer size={13} stroke={1.8} /> {topic.action.label}
                    </button>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      ))}
    </div>
  );
}

const previewLines = 9;

function ExampleCode({ example }: { example: GuideExample }) {
  const [all, setAll] = useState(false);
  const lines = example.query.trimEnd().split("\n");
  const long = lines.length > previewLines;
  const shown = all || !long ? lines.join("\n") : lines.slice(0, previewLines).join("\n");
  return (
    <div className="gx-topic-code">
      <pre dangerouslySetInnerHTML={{ __html: highlight(shown, "graphql") }} />
      {example.variables && (
        <pre className="gx-topic-vars" dangerouslySetInnerHTML={{ __html: '<span class="tok-c"># variables</span>\n' + highlight(example.variables, "json") }} />
      )}
      {long && (
        <button className="link-button gx-topic-more" onClick={() => setAll(!all)}>
          {all ? "Show less" : `Show all ${lines.length} lines`}
        </button>
      )}
    </div>
  );
}

// The editor a projection opens in, by the language the engine names for its file (GET /definitions, PROTOCOL 3.50). This is the one place the
// canvas maps a projection language to editor support: KQL is the only entry. Any other language opens as its text, read-only, with a note.
import { MessageBar, MessageBarBody, Spinner, Text, makeStyles, tokens } from "@fluentui/react-components";
import { lazy, useEffect, useState, type ComponentType } from "react";
import type { Graph } from "../../core/graph";
import type { EditorService, ProjectionFile } from "../../editor/service";

/** What every editor of a projection is given. `draft`: read the text as the draft holds it; `store`: keep a changed text in the draft. */
export interface EditorProps {
  port: EditorService; file: ProjectionFile; draft?: string;
  /** preview the stored text and answer the rows after each engine operator */
  rows?: () => Promise<{ op: string; rows: string }[]>;
  /** the element picked in a data graph (an edge brings its ends) */
  picked?: { graph: string; data: Graph; ids: string[] } | null;
  /** store the text in the draft; the engine's refusal, or null */
  store?: (text: string) => Promise<string | null>;
}

// the KQL editor brings CodeMirror: it loads when a KQL projection is first opened
const EDITORS: Readonly<Record<string, ComponentType<EditorProps>>> = { kql: lazy(() => import("./KqlText")) };

/** The editor for a projection file's language; a language without one gets the read-only text. */
export const editorFor = (language: string): ComponentType<EditorProps> => EDITORS[language] ?? ReadOnlyText;

const useStyles = makeStyles({
  root: { display: "grid", gridTemplateRows: "auto auto minmax(0, 1fr)", height: "100%", minHeight: 0, rowGap: tokens.spacingVerticalS, padding: tokens.spacingHorizontalM, boxSizing: "border-box", backgroundColor: tokens.colorNeutralBackground3 },
  text: { margin: 0, overflow: "auto", minHeight: 0, padding: tokens.spacingHorizontalS, fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, border: `1px solid ${tokens.colorNeutralStroke1}`, backgroundColor: tokens.colorNeutralBackground1 },
});

/** A projection file in a language the canvas has no editor for: its text as the space holds it, which the engine still checks and runs. */
export function ReadOnlyText({ port, file }: EditorProps) {
  const s = useStyles();
  const [text, setText] = useState<{ status: "loading" } | { status: "ready"; text: string } | { status: "error"; message: string }>({ status: "loading" });
  useEffect(() => {
    let gone = false;
    setText({ status: "loading" });
    port.definitionText(file.file).then((t) => !gone && setText({ status: "ready", text: t }), (e: Error) => !gone && setText({ status: "error", message: e.message }));
    return () => { gone = true; };
  }, [port, file.file]);
  return (
    <div className={s.root}>
      <Text font="monospace">{file.file}</Text>
      <MessageBar intent="info"><MessageBarBody>The canvas has no editor for {file.language} projections, so this text is read-only.</MessageBarBody></MessageBar>
      {text.status === "loading" && <Spinner size="small" label="Reading the projection's text…" />}
      {text.status === "error" && <MessageBar intent="warning"><MessageBarBody>{text.message}</MessageBarBody></MessageBar>}
      {text.status === "ready" && <pre className={s.text}>{text.text}</pre>}
    </div>
  );
}

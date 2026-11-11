import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, DialogTrigger, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text } from "@fluentui/react-components";
import { dispatch, useStore } from "../state/store";
import { SHORTCUTS } from "./shortcuts";

export function Dialogs() {
  const dialog = useStore((s) => s.dialog);
  const rejections = useStore((s) => s.rejections);
  const onOpenChange = (_: unknown, d: { open: boolean }) => { if (!d.open) dispatch({ type: "ui/dialog", dialog: null }); };
  return (
    <>
      <Dialog open={dialog === "shortcuts"} onOpenChange={onOpenChange}>
        <DialogSurface><DialogBody>
          <DialogTitle>Keyboard shortcuts</DialogTitle>
          <DialogContent>
            <Table size="small" aria-label="Keyboard shortcuts"><TableHeader><TableRow><TableHeaderCell>Keys</TableHeaderCell><TableHeaderCell>Does</TableHeaderCell><TableHeaderCell>Where</TableHeaderCell></TableRow></TableHeader>
              <TableBody>{SHORTCUTS.map((k, i) => <TableRow key={i}><TableCell><kbd>{k.keys}</kbd></TableCell><TableCell>{k.does}</TableCell><TableCell>{k.where}</TableCell></TableRow>)}</TableBody></Table>
          </DialogContent>
          <DialogActions><DialogTrigger disableButtonEnhancement><Button appearance="primary">Close</Button></DialogTrigger></DialogActions>
        </DialogBody></DialogSurface>
      </Dialog>
      <Dialog open={dialog === "rejections"} onOpenChange={onOpenChange}>
        <DialogSurface><DialogBody>
          <DialogTitle>Rejected records ({rejections.length})</DialogTitle>
          <DialogContent>
            {rejections.length === 0 ? <Text>Every record passed schema validation and applied cleanly.</Text> : (
              <ul>{rejections.slice(-50).map((r, i) => <li key={i}><Text weight="semibold">{r.source}: {r.where}</Text><div>{r.errors.join(" · ")}</div></li>)}</ul>
            )}
          </DialogContent>
          <DialogActions><DialogTrigger disableButtonEnhancement><Button appearance="primary">Close</Button></DialogTrigger></DialogActions>
        </DialogBody></DialogSurface>
      </Dialog>
    </>
  );
}

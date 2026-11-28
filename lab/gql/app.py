#!/usr/bin/env python3
"""Minimal terminal shell for the experimental GQL health graph."""
from argparse import ArgumentParser
from pathlib import Path
import sys

from gql_lab import GqlHealthLab, LabError, compact, instant_text


def line(value):
    print(compact(value))


class Terminal:
    def __init__(self, lab):
        self.lab = lab
        self.graph = "master"

    def help(self):
        print("help")
        print("timeline                  visible native/record arrivals")
        print("time latest|<instant>     re-run the GQL projection at this as-of point")
        print("use master|health        choose the graph queried by gql")
        print("gql <one-line GQL>        run a read-only GQL query")
        print("view [master|health]      render the selected graph as text")
        print("info                     show scope and source-availability limitations")
        print("quit")

    def info(self):
        print("GQL (not Cypher). Raw input is never modified; terminal GQL sessions are read-only.")
        print("The default database is in memory. A --database path is an explicit local persistence choice.")
        print("Inventory alone supplies no application trace. Request rows are telemetry, not trace coverage.")
        print("Network flows are raw evidence, not proof of a healthy application.")
        print("CPU policy is illustrative: verified average Percent, 80 warning / 95 critical, 300 s stale.")

    def timeline(self):
        rows = self.lab.timeline()
        if not rows:
            print("(no captures)")
            return
        for row in rows:
            print(f"{row['captured_at']}  {row['category']:<16} {row['source']:<24} {row['arrivals']}")

    def change_time(self, point):
        selected = None if point == "latest" else point
        assessments = self.lab.project(selected)
        print(f"as-of {instant_text(self.lab.current_as_of_ns)}: {len(assessments)} assessments")

    def view(self, graph):
        if graph not in ("master", "health"):
            raise LabError("view needs master or health")
        if graph == "master":
            summary = self.lab.master_summary()
            print(f"MASTER {summary['observations']} normalized observations")
            for category in summary["categories"]:
                print(f"  [RawCapture/{category['category']}] arrivals={category['arrivals']}")
            for trace in summary["network_traces"]:
                key = trace["trace_key"] or "(raw)"
                origin = "raw" if "RawCapture" in trace["labels"] else "normalized"
                print(f"  ({origin} NetworkTrace {key}) -- at {trace['trace_at']} -- {trace['verdict']}")
            if not summary["network_traces"]:
                print("  (no raw network trace evidence supplied)")
            for fact in summary["network_facts"]:
                print(f"  ({fact['resource_id']}) -- {fact['fact_kind']} --> ({fact['target']})")
            for relation in summary["relationships"]:
                verdict = f" verdict={relation['verdict']}" if relation["verdict"] else ""
                print(f"  ({relation['source_key']}) -[{relation['relation']}{verdict}]-> "
                      f"({relation['target_key']})")
            return
        data = self.lab.health_view()
        for assessment in data["assessments"]:
            print(f"[{assessment['state']}] {assessment['resource_id']} :: {assessment['metric']}")
            print(f"  dims={assessment['dimensions_json']}")
            print(f"  {assessment['reason']}")
            print(f"  evidence={assessment['evidence_count']}")
        if not data["assessments"]:
            print("(no health subjects visible at this as-of point)")
        by_resource = {}
        for evidence in data["evidence"]:
            by_resource.setdefault(evidence["resource_id"], []).append(evidence)
        for resource, evidence_rows in by_resource.items():
            for evidence in evidence_rows:
                print(f"  ({resource}) <-- SUPPORTED_BY -- "
                      f"({evidence['row_type']} event={evidence['event_at']} raw={evidence['raw_capture_id']})")
        for relation in data["topology"]:
            verdict = f" verdict={relation['verdict']}" if relation["verdict"] else ""
            print(f"  topology ({relation['source_key']}) "
                  f"-[{relation['relation']}{verdict}]-> ({relation['target_key']})")
        for status in data["source_status"]:
            print(f"  [source/{status['kind']}: {status['state']}] {status['detail']}")

    def command(self, raw):
        text = raw.strip()
        if not text:
            return True
        if text in ("quit", "exit"):
            return False
        if text == "help":
            self.help()
        elif text == "info":
            self.info()
        elif text == "timeline":
            self.timeline()
        elif text.startswith("time "):
            self.change_time(text.partition(" ")[2].strip())
        elif text.startswith("use "):
            graph = text.partition(" ")[2].strip()
            if graph not in ("master", "health"):
                raise LabError("graph must be master or health")
            self.graph = graph
            print(f"using {graph}")
        elif text.startswith("gql "):
            rows = self.lab.query(self.graph, text.partition(" ")[2])
            if not rows:
                print("(no rows)")
            for row in rows:
                line(row)
        elif text.startswith("view"):
            self.view(text.partition(" ")[2].strip() or self.graph)
        else:
            raise LabError("unknown command; try help")
        return True


def database_path(path):
    if not path:
        return None
    target = Path(path).expanduser().resolve()
    root = Path(__file__).resolve().parents[2]
    try:
        relative = target.relative_to(root)
    except ValueError:
        return target
    allowed = Path("lab/gql/.local")
    if relative != allowed and allowed not in relative.parents:
        raise LabError("database paths inside the repository must be below lab/gql/.local")
    return target


def protect_input(input_path, database):
    if not database or not input_path:
        return
    source = Path(input_path).expanduser().resolve()
    if database == source or database in source.parents or source in database.parents:
        raise LabError("the persistent database path must not overlap the raw input")


def main(argv):
    parser = ArgumentParser(description=__doc__)
    inputs = parser.add_mutually_exclusive_group(required=True)
    inputs.add_argument("--input", type=Path, help="native capture or record-envelope .ndjson file/directory")
    inputs.add_argument("--collector-replay", type=Path, help="collector source-recording directory")
    parser.add_argument("--streams", type=Path, help="streams.json for --collector-replay")
    parser.add_argument("--as-of", help="initial ISO 8601 as-of instant (default: latest arrival)")
    parser.add_argument("--database", help="explicit persistent Grafeo path; default is in-memory")
    parser.add_argument("--command", action="append", default=[], help="run a shell command instead of waiting")
    args = parser.parse_args(argv)

    try:
        persistent = database_path(args.database)
        protect_input(args.input or args.collector_replay, persistent)
        with GqlHealthLab(database_path=persistent) as lab:
            if args.input:
                lab.load_path(args.input)
            else:
                lab.load_collector_replay(args.collector_replay, args.streams)
            lab.project(args.as_of)
            terminal = Terminal(lab)
            if args.command:
                for command in args.command:
                    terminal.command(command)
                return 0
            print(f"loaded {lab.raw_count} captures; latest "
                  f"{instant_text(lab.latest_captured_ns)}; active graph master")
            terminal.help()
            while True:
                try:
                    raw = input("gql> ")
                except EOFError:
                    break
                try:
                    if not terminal.command(raw):
                        break
                except LabError as exc:
                    print(f"error: {exc}", file=sys.stderr)
    except (LabError, OSError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

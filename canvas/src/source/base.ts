import type { Rejection } from "../core/validate";
import type { EvaluateResult } from "./types";
import { NOT_AVAILABLE } from "./types";

/** Shared rejection reporting for source implementations. */
export class RejectionBus {
  private listeners = new Set<(r: Rejection) => void>();
  readonly all: Rejection[] = [];
  emit(r: Rejection): void { this.all.push(r); for (const l of this.listeners) l(r); }
  on(cb: (r: Rejection) => void): () => void {
    for (const r of this.all) cb(r); // late subscribers still see earlier rejections
    this.listeners.add(cb);
    return () => this.listeners.delete(cb);
  }
}

export const evaluateUnavailable = async (): Promise<EvaluateResult> => NOT_AVAILABLE;

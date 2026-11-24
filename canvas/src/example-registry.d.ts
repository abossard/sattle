declare module "virtual:canvas-examples" {
  export const connections: readonly { id: string; label: string; url: string }[];
  export const guidePacks: readonly import("./tutorial/lessons").GuidePack[];
  export const sourceProviders: readonly import("./state/sources").SourceRegistration[];
}

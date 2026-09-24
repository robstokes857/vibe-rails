export interface Repository {
    name: string;
    description?: string;
    branch?: string;
    revision?: string;
    generatedAt?: string;
    sample?: boolean;
    [key: string]: unknown;
}
export interface HistoryEntry {
    commit: string;
    message: string;
    author: string;
    date: string;
    url?: string;
    [key: string]: unknown;
}
export interface CodeNode {
    id: string;
    name: string;
    kind: string;
    parentId?: string;
    language?: string;
    path?: string;
    summary?: string;
    signature?: string;
    metrics?: Record<string, number>;
    changes?: Record<string, number>;
    history?: HistoryEntry[];
    links?: { label: string; url: string }[];
    [key: string]: unknown;
}
export interface CodeEdge {
    id: string;
    source: string;
    target: string;
    kind: string;
    [key: string]: unknown;
}
export interface CodeGraph {
    schemaVersion: '1.0';
    repository: Repository;
    nodes: CodeNode[];
    edges: CodeEdge[];
    [key: string]: unknown;
}
export type DetailsTab = 'overview' | 'connections' | 'history';
export interface EntityDetails {
    schemaVersion: '1.0';
    repository: Repository;
    node: CodeNode;
    parent: CodeNode | null;
    children: CodeNode[];
    relationships: {
        edge: CodeEdge;
        direction: 'incoming' | 'outgoing' | 'self';
        node: CodeNode;
    }[];
}
export interface DetailsRequest extends EntityDetails { tab: DetailsTab; }
export interface ChangeSummary {
    /** User preference; an empty list has no visual effect even when enabled. */
    enabled: boolean;
    changedFiles: string[];
    matchedFiles: string[];
    unmatchedFiles: string[];
    /** Nodes in changed files, including symbols whose path is inherited. */
    nodeIds: string[];
}
export type ThemeMode = 'dark' | 'light';
/** Colors use #RGB, #RGBA, #RRGGBB or #RRGGBBAA. */
export interface ThemeColors {
    background: string;
    surface: string;
    surfaceHover: string;
    elevated: string;
    primary: string;
    primaryHover: string;
    onPrimary: string;
    accent: string;
    text: string;
    muted: string;
    border: string;
    success: string;
    warning: string;
    danger: string;
    shadow: string;
}
export interface ThemeNodes { module: string; class: string; file: string; function: string; data: string; }
export interface ThemeGraph { edge: string; crossEdge: string; changed: string; }
export interface ThemeOptions {
    /** Selects the base palette and native-control color scheme. Default dark. */
    mode?: ThemeMode;
    colors?: Partial<ThemeColors>;
    nodes?: Partial<ThemeNodes>;
    graph?: Partial<ThemeGraph>;
}
export interface ResolvedTheme {
    mode: ThemeMode;
    colors: ThemeColors;
    nodes: ThemeNodes;
    graph: ThemeGraph;
}
export interface CodeAtlasController {
    /** Resolves after layout, the startup reveal and the host connection finish; controls are usable. */
    readonly ready: Promise<void>;
    focusNode(id: string): Promise<void>;
    getDetails(id: string): Promise<EntityDetails>;
    /** Requests details; callback completion is separate and errors go to onError. */
    openDetails(id: string, tab?: DetailsTab): Promise<void>;
    back(): Promise<boolean>;
    /** Replaces the overlay list, preserving the toggle, camera, selection and layout. */
    setChangedFiles(files: string[]): Promise<ChangeSummary>;
    setHighlightChanges(enabled: boolean): Promise<ChangeSummary>;
    getChangeSummary(): Promise<ChangeSummary>;
    /** Replaces overrides over the selected base palette. Omit/reset to {} for default dark. */
    setTheme(theme?: ThemeOptions): Promise<ResolvedTheme>;
    getTheme(): Promise<ResolvedTheme>;
    /** Idempotent. Removes this instance's frame, timers and pending requests. */
    destroy(): void;
}
export interface MountOptions {
    /** Optional host CSP nonce for trusted renderer scripts (e.g. VibeRails __viberails_NONCE__). */
    cspNonce?: string;
    graph: CodeGraph;
    title?: string;
    theme?: ThemeOptions;
    /** Optional repository-relative paths; exact, case-sensitive matching after slash normalization. */
    changedFiles?: string[];
    /** Default true. Users can switch it off with Highlight changes in the toolbar. */
    highlightChanges?: boolean;
    /** Base64 PNG/JPEG/WebP/GIF data URL, at most 512 KiB. */
    logo?: string;
    /** Omit to keep the built-in details dialog. Return value is ignored. */
    onOpenDetails?: (details: DetailsRequest, atlas: CodeAtlasController) => void | Promise<void>;
    onError?: (error: Error) => void;
}
/** Mounts in a connected element with an explicit, nonzero height. Invalid inputs throw synchronously. */
export function mountCodeAtlas(container: HTMLElement, options: MountOptions): CodeAtlasController;

/** Snapshots semantic host CSS variables into portable hex colors. Does not watch for changes. */
export function themeFromCss(element?: Element, options?: { mode?: ThemeMode }): ThemeOptions;

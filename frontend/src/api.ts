export const SESSION_EXPIRED = "tablespace:session-expired";
let token = "";
let sessionGeneration = 0;
export async function csrf() {
  const generation = sessionGeneration;
  const r = await fetch("/api/auth/csrf", { credentials: "same-origin" });
  if (!r.ok) throw new Error("Unable to refresh the security token.");
  const result = await r.json();
  if (generation !== sessionGeneration)
    throw new Error("Session changed. Please try again.");
  token = result.token;
}
export async function api<T = unknown>(
  url: string,
  method = "GET",
  body?: unknown,
): Promise<T> {
  const generation = sessionGeneration;
  if (method !== "GET" && !token) await csrf();
  const r = await fetch("/api" + url, {
    method,
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (generation !== sessionGeneration)
    throw new Error("Session changed. Please try again.");
  if (!r.ok) {
    if (r.status === 401 && url !== "/auth/login") {
      token = "";
      sessionGeneration++;
      window.dispatchEvent(new Event(SESSION_EXPIRED));
    }
    let title =
      r.status === 401 ? "Please sign in." : `Request failed (${r.status})`;
    try {
      title = (await r.json()).title || title;
    } catch {
      /* empty response */
    }
    throw new Error(title);
  }
  if (url === "/auth/login" || url === "/auth/logout") {
    sessionGeneration++;
    token = "";
  }
  return r.status === 204 ? (undefined as T) : r.json();
}
export type User = {
  id: number;
  username: string;
  isAdmin: boolean;
  enabled: boolean;
};
export type Connection = {
  id: number;
  name: string;
  database: string;
  host?: string;
  port?: number;
  username?: string;
  verifyTls?: boolean;
};
export type Column = {
  canWrite?: boolean;
  name: string;
  type: string;
  nullable: boolean;
  primaryKey: boolean;
  generated: boolean;
  autoIncrement: boolean;
  default: string | null;
};
export type Row = {
  keyToken?: string | null;
  values: Record<string, unknown>;
  version: string;
  displayValues?: Record<string, string | null>;
  joinedValues?: Record<string, unknown>;
  calculationErrors?: Record<string, string>;
};
export type Page = {
  hasPrimaryKey?: boolean;
  total: number;
  page: number;
  size: number;
  columns: Column[];
  rows: Row[];
  joinedColumns?: Column[];
  sort?: string;
  descending?: boolean;
};
export type Sumup = {
  operation: "sum" | "count";
  childTable: string;
  lookupField: string;
  sourceField?: string | null;
};
export type InputMask = {
  characterSet?: "letters" | "digits" | "alphanumeric" | "";
  minimumLength?: number;
  requiredCharacters?: string;
  pattern?: string | null;
};
export type Field = {
  creationDefault?: { value?: string | null; isNull?: boolean } | null;
  mask?: InputMask | null;
  sumup?: Sumup | null;
  name: string;
  label: string;
  section: string;
  order: number;
  hidden: boolean;
  readOnly: boolean;
  widget: string;
  lookup?: Lookup | null;
  options?: DropdownOption[] | null;
  showInList?: boolean;
  listOrder?: number | null;
  join?: Join | null;
  required?: boolean;
  enabledOptionKeys?: string[] | null;
  formula?: string | null;
};
export type ObjectField = Omit<
  Field,
  "section" | "order" | "hidden" | "showInList" | "listOrder"
>;
export type DataTypeField = {
  name: string;
  required: boolean;
  mask: InputMask | null;
  overrideDropdownOptions: boolean;
  enabledOptionKeys: string[];
};
export type DataTypeDefinition = {
  key: string;
  label: string;
  fields: DataTypeField[];
};
export type ObjectDefinition = {
  fields: ObjectField[];
  dataTypes: DataTypeDefinition[];
  defaultDataTypeKey: string;
  view?: ListView | null;
  sumupsPending?: boolean;
};
export type FieldPresentation = {
  name: string;
  label: string;
  section: string;
  editorOrder: number;
  showInEditor: boolean;
  showInList: boolean;
  listOrder?: number | null;
};
export type LayoutPresentation = { fields: FieldPresentation[] };
export type Grant = {
  fields?: Record<string, "none" | "read" | "write"> | null;
  userId: number;
  connectionId: number;
  table: string;
  read: boolean;
  create: boolean;
  update: boolean;
  delete: boolean;
};

export type Lookup = {
  table: string;
  keyColumn: string;
  displayColumn: string;
  searchColumns: string[];
  copyMappings?:
    | { sourceColumn: string; destinationColumn: string; editable?: boolean }[]
    | null;
  criteria?: ListView | null;
};

export type DropdownOption = { key: string; display: string };

export type Join = {
  sourceColumn: string;
  table: string;
  keyColumn: string;
  valueColumn: string;
};

export type ListFilter = {
  column: string;
  operator: string;
  value?: string | null;
};
export type ListView = {
  label?: string | null;
  sort?: string | null;
  descending?: boolean;
  match?: "all" | "any";
  filters?: ListFilter[] | null;
};

export type RelatedTab = {
  lookupField?: string | null;
  id: string;
  label: string;
  table: string;
  parentColumn: string;
  relatedColumn: string;
  columns: string[];
  targetPageId?: number | null;
  linkColumn?: string | null;
};
export type RecordPageDefinition = {
  id: number;
  connectionId: number;
  table: string;
  name: string;
  linkColumn: string;
  tabs: RelatedTab[];
};
export type PageSummary = Omit<RecordPageDefinition, "tabs">;

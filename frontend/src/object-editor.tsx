import { useEffect, useRef, useState } from "react";
import {
  api,
  type Column,
  type Connection,
  type ObjectDefinition,
  type ObjectField,
  type DataTypeDefinition,
  type FieldSetRule,
  type Field,
  type ListView,
} from "./api";
import { ListViewEditor } from "./list-view";
import { JoinConfiguration } from "./layout-fields";
import { DropdownConfiguration, dropdownError } from "./field-controls";
import { LookupConfiguration } from "./lookups";
import { CreationDefaultEditor } from "./creation-defaults";
import { FormulaValidator } from "./formula-validator";
import { SumupConfiguration } from "./sumups";
import { DataTypesEditor, normalizeDataTypes, reconcileDataTypes } from "./data-types";
import { FieldSetRulesEditor } from "./field-set-rules";

type SchemaColumn = {
  name: string;
  type: string;
  sqlType: string;
  nullable: boolean;
  primaryKey: boolean;
  autoIncrement: boolean;
  generated: boolean;
  length: number | null;
  precision: number | null;
  scale: number | null;
  default: string | null;
  relatedTable: string | null;
  relatedKey: string | null;
  uniqueKey: boolean;
  editBlocked: string | null;
};
type Schema = { version: string; columns: SchemaColumn[] };
type Draft = {
  name: string;
  type: string;
  nullable: boolean;
  length: number;
  precision: number;
  scale: number;
  relatedTable: string;
  relatedKey: string;
  displayColumn: string;
};
const blank = (): Draft => ({
  name: "",
  type: "text",
  nullable: true,
  length: 255,
  precision: 18,
  scale: 2,
  relatedTable: "",
  relatedKey: "",
  displayColumn: "",
});
const canonicalObjectFields = (fields: ObjectField[]) => fields.map((field) => {
  const canonical = { ...field };
  delete canonical.required;
  delete canonical.mask;
  return canonical;
});
const textColumnTypes = new Set([
  "char",
  "varchar",
  "tinytext",
  "text",
  "mediumtext",
  "longtext",
]);
const maskEligibleFields = (fields: ObjectField[], schema: Schema | null) =>
  new Set(
    fields
      .filter((field) => {
        const column = schema?.columns.find((candidate) => candidate.name === field.name);
        return !!column
          && textColumnTypes.has(column.type)
          && !column.generated
          && !column.autoIncrement
          && !field.readOnly
          && ["auto", "text", "textarea"].includes(field.widget);
      })
      .map((field) => field.name),
  );
const kind = (c: SchemaColumn) =>
  ({
    varchar: "text",
    bigint: "integer",
    int: "integer",
    smallint: "integer",
    mediumint: "integer",
    tinyint: "boolean",
    text: "longtext",
    timestamp: "datetime",
  })[c.type] || c.type;
export function ObjectEditor({
  connections,
  onChanged,
}: {
  connections: Connection[];
  onChanged: () => void;
}) {
  const [connection, setConnection] = useState(connections[0]?.id || 0);
  const [tables, setTables] = useState<string[]>([]),
    [table, setTable] = useState("");
  const [schema, setSchema] = useState<Schema | null>(null),
    [target, setTarget] = useState<Schema | null>(null),
    [objectFields, setObjectFields] = useState<ObjectField[]>([]),
    [dataTypes, setDataTypes] = useState<DataTypeDefinition[]>([]),
    [defaultDataTypeKey, setDefaultDataTypeKey] = useState(""),
    [fieldSetRules, setFieldSetRules] = useState<FieldSetRule[]>([]),
    [objectView, setObjectView] = useState<ListView>({}),
    [objectLoading, setObjectLoading] = useState(false);
  const [draft, setDraft] = useState<Draft | null>(null),
    [editing, setEditing] = useState(false),
    [fieldDraft, setFieldDraft] = useState<ObjectField | null>(null),
    [pendingVirtual, setPendingVirtual] = useState(false);
  const [creating, setCreating] = useState(false),
    [newName, setNewName] = useState(""),
    [primaryKey, setPrimaryKey] = useState("id");
  const [busy, setBusy] = useState(false),
    [loading, setLoading] = useState(false),
    [error, setError] = useState(""),
    [message, setMessage] = useState(""),
    [revision, setRevision] = useState(0);
  const fieldDialogRef = useRef<HTMLFormElement>(null);
  const base = `/admin/connections/${connection}/schema`;
  useEffect(() => {
    let active = true;
    setTables([]);
    setTable("");
    setSchema(null);
    setDraft(null);
    setCreating(false);
    setError("");
    setMessage("");
    if (connection)
      api<string[]>(`/connections/${connection}/tables`)
        .then((t) => {
          if (active) {
            setTables(t);
            setTable(t[0] || "");
          }
        })
        .catch((e) => {
          if (active) setError(e.message);
        });
    return () => {
      active = false;
    };
  }, [connection]);
  useEffect(() => {
    let active = true;
    setSchema(null);
    setDraft(null);
    setObjectFields([]);
    setDataTypes([]);
    setDefaultDataTypeKey("");
    setFieldSetRules([]);
    setObjectView({});
    setError("");
    setLoading(true);
    setObjectLoading(true);
    if (table)
      Promise.all([
        api<Schema>(`${base}/tables/${encodeURIComponent(table)}`),
        api<ObjectDefinition>(
          `/admin/connections/${connection}/tables/${encodeURIComponent(table)}/object`,
        ),
      ])
        .then(([nextSchema, object]) => {
          if (active) {
            const normalized = normalizeDataTypes(
              object.fields,
              object.dataTypes,
              object.defaultDataTypeKey,
              maskEligibleFields(object.fields, nextSchema),
            );
            setSchema(nextSchema);
            setObjectFields(canonicalObjectFields(object.fields));
            setDataTypes(normalized.dataTypes);
            setDefaultDataTypeKey(normalized.defaultDataTypeKey);
            setFieldSetRules(object.fieldSetRules || []);
            setObjectView(object.view || {});
          }
        })
        .catch((e) => {
          if (active) setError(e.message);
        })
        .finally(() => {
          if (active) {
            setLoading(false);
            setObjectLoading(false);
          }
        });
    else {
      setLoading(false);
      setObjectLoading(false);
    }
    return () => {
      active = false;
    };
  }, [base, table, revision]);
  const targetTable = draft?.relatedTable || "";
  useEffect(() => {
    let active = true;
    setTarget(null);
    if (targetTable)
      api<Schema>(`${base}/tables/${encodeURIComponent(targetTable)}`)
        .then((s) => {
          if (active) setTarget(s);
        })
        .catch((e) => {
          if (active) setError(e.message);
        });
    return () => {
      active = false;
    };
  }, [base, targetTable]);
  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await action();
      setRevision((value) => value + 1);
      onChanged();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function saveDataTypes(
    nextDataTypes: DataTypeDefinition[],
    nextDefaultDataTypeKey: string,
  ) {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await api(
        `/admin/connections/${connection}/tables/${encodeURIComponent(table)}/object`,
        "PUT",
        {
          fields: canonicalObjectFields(objectFields),
          dataTypes: nextDataTypes,
          defaultDataTypeKey: nextDefaultDataTypeKey,
          fieldSetRules,
          view: objectView,
        },
      );
      const nextSchema = await api<Schema>(
        `${base}/tables/${encodeURIComponent(table)}`,
      );
      setSchema(nextSchema);
      setDataTypes(nextDataTypes);
      setDefaultDataTypeKey(nextDefaultDataTypeKey);
      setMessage("Data types updated.");
      onChanged();
    } catch (reason) {
      setError((reason as Error).message);
      throw reason;
    } finally {
      setBusy(false);
    }
  }
  async function saveFieldSetRules(nextRules: FieldSetRule[]) {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await api(
        "/admin/connections/" + connection + "/tables/" + encodeURIComponent(table) + "/object",
        "PUT",
        {
          fields: canonicalObjectFields(objectFields),
          dataTypes: reconcileDataTypes(
            dataTypes,
            objectFields,
            maskEligibleFields(objectFields, schema),
          ),
          defaultDataTypeKey,
          fieldSetRules: nextRules,
          view: {
            ...objectView,
            label: objectView.label?.trim(),
          },
        },
      );
      setFieldSetRules(nextRules);
      setMessage("Field set rule saved.");
      onChanged();
      return null;
    } catch (reason) {
      const message = (reason as Error).message;
      setError(message);
      return message;
    } finally {
      setBusy(false);
    }
  }
  const patch = (value: Partial<Draft>) =>
    setDraft((old) => (old ? { ...old, ...value } : old));
  const columns: Column[] = (schema?.columns || []).map((column) => ({
    name: column.name,
    type: column.type,
    nullable: column.nullable,
    primaryKey: column.primaryKey,
    generated: column.generated,
    autoIncrement: column.autoIncrement,
    default: column.default,
  }));
  const draftColumn: Column | undefined = draft && fieldDraft
    ? columns.find((column) => column.name === fieldDraft.name) || {
        name: fieldDraft.name,
        type: ({ text: "varchar", email: "varchar", dropdown: "varchar", textarea: "longtext", number: "decimal", checkbox: "tinyint", date: "date", datetime: "datetime", lookup: "bigint" } as Record<string, string>)[fieldDraft.widget] || "varchar",
        nullable: draft.nullable,
        primaryKey: false,
        generated: false,
        autoIncrement: false,
        default: null,
      }
    : undefined;
  const editingSchemaColumn =
    draft && schema
      ? schema.columns.find((column) => column.name === draft.name)
      : undefined;
  const showDatabaseAttributes = !editing || !!editingSchemaColumn;
  const databaseAttributesDisabled =
    busy || (editing && !!editingSchemaColumn?.editBlocked);
  const runtimeFields: Field[] = objectFields.map((field, index) => ({
    ...field,
    section: "",
    order: index,
    hidden: false,
    showInList: true,
    listOrder: index,
  }));
  const fieldSetCandidates = [
    ...objectFields.filter((field) => {
      const column = schema?.columns.find((candidate) => candidate.name === field.name);
      return !!column
        && !column.generated
        && !column.autoIncrement
        && !column.primaryKey
        && !field.readOnly
        && !field.lookup
        && !column.type.toLowerCase().includes("blob")
        && !["binary", "varbinary", "geometry"].includes(column.type.toLowerCase())
        && !["join", "formula", "sumup"].includes(field.widget);
    }).map((field) => ({ name: field.name, label: field.label || field.name })),
    ...(dataTypes.length > 0 ? [{ name: "datatype", label: "Data type" }] : []),
  ];
  const patchField = (patch: Partial<ObjectField>) =>
    setFieldDraft((old) => (old ? { ...old, ...patch } : old));
  const databaseType = (widget: string) =>
    ({
      text: "text",
      email: "text",
      dropdown: "text",
      textarea: "longtext",
      number: "decimal",
      checkbox: "boolean",
      date: "date",
      datetime: "datetime",
      lookup: "relation",
    })[widget] || "text";
  const closeFieldDialog = () => {
    if (pendingVirtual && fieldDraft)
      setObjectFields((old) =>
        old.filter((field) => field.name !== fieldDraft.name),
      );
    setDraft(null);
    setFieldDraft(null);
    setPendingVirtual(false);
  };
  const fieldDialogOpen = !!draft && !!fieldDraft && !!schema && !creating;
  useEffect(() => {
    if (!fieldDialogOpen) return;
    const previous = document.activeElement as HTMLElement | null;
    fieldDialogRef.current
      ?.querySelector<HTMLElement>("input:not(:disabled),button:not(:disabled)")
      ?.focus();
    return () => previous?.focus();
  }, [fieldDialogOpen]);
  return (
    <div className="object-editor">
      <div className="heading">
        <div className="eyebrow">ADMINISTRATION</div>
        <h1>Object editor</h1>
        <p>
          Create database structure and define the object behavior used by the
          application.
        </p>
      </div>
      <div className="selectors">
        <label>
          Connection
          <select
            disabled={busy}
            value={connection}
            onChange={(e) => setConnection(Number(e.target.value))}
          >
            <option value={0}>Select connection</option>
            {connections.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          Table
          <select
            disabled={busy}
            value={table}
            onChange={(e) => {
              setTable(e.target.value);
              setCreating(false);
              setMessage("");
            }}
          >
            <option value="">Select table</option>
            {tables.map((t) => (
              <option key={t}>{t}</option>
            ))}
          </select>
        </label>
      </div>
      {error && (
        <div className="alert" role="alert">
          {error}
        </div>
      )}
      {message && (
        <p className="notice" role="status">
          {message}
        </p>
      )}
      <div className="toolbar">
        <div className="actions">
          <button
            className="primary"
            disabled={!connection || busy}
            onClick={() => {
              setCreating(true);
              setDraft(null);
              setNewName("");
              setPrimaryKey("id");
              setError("");
            }}
          >
            New table
          </button>
          <button
            disabled={!table || busy}
            onClick={() => setRevision((v) => v + 1)}
          >
            Refresh structure
          </button>
        </div>
      </div>
      {creating && (
        <form
          className="card"
          aria-label="Create table"
          onSubmit={(e) => {
            e.preventDefault();
            void run(async () => {
              const result = await api<Schema>(`${base}/tables`, "POST", {
                name: newName,
                primaryKey,
              });
              setTables((old) => [...old, newName].sort());
              setTable(newName);
              setSchema(result);
              setCreating(false);
              setMessage("Table created with an auto-increment primary key.");
            });
          }}
        >
          <h2>New table</h2>
          <div className="form-grid">
            <label>
              Table name
              <input
                aria-label="New table name"
                required
                pattern="[A-Za-z_][A-Za-z0-9_]{0,63}"
                maxLength={64}
                value={newName}
                onChange={(e) => setNewName(e.target.value)}
                disabled={busy}
              />
            </label>
            <label>
              Primary key name
              <input
                required
                aria-label="Primary key name"
                pattern="[A-Za-z_][A-Za-z0-9_]{0,63}"
                maxLength={64}
                value={primaryKey}
                onChange={(e) => setPrimaryKey(e.target.value)}
                disabled={busy}
              />
            </label>
          </div>
          <p className="muted">
            A unique BIGINT primary key is generated automatically for each new
            record.
          </p>
          <div className="form-actions">
            <button
              type="button"
              disabled={busy}
              onClick={() => setCreating(false)}
            >
              Cancel
            </button>
            <button className="primary" disabled={busy}>
              Create table
            </button>
          </div>
        </form>
      )}
      {loading && <p role="status">Loading table structure…</p>}
      {draft && fieldDraft && schema && !creating && (
        <div
          className="overlay"
          role="presentation"
          onKeyDown={(event) => {
            if (event.key === "Escape") closeFieldDialog();
            if (event.key !== "Tab") return;
            const controls = fieldDialogRef.current?.querySelectorAll<HTMLElement>(
              "button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled)",
            );
            if (!controls?.length) return;
            const first = controls[0];
            const last = controls[controls.length - 1];
            if (event.shiftKey && document.activeElement === first) {
              event.preventDefault();
              last.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
              event.preventDefault();
              first.focus();
            }
          }}
        >
        <form
          ref={fieldDialogRef}
          className="modal schema-column-form"
          role="dialog"
          aria-modal="true"
          aria-label={editing ? "Edit database field" : "Add field"}
          onSubmit={(e) => {
            e.preventDefault();
            void run(async () => {
              const currentColumn = schema.columns.find(
                (column) => column.name === draft.name,
              );
              const physical = !!currentColumn;
              let result = schema;
              const blocked = currentColumn?.editBlocked;
              const schemaDirty =
                !!currentColumn &&
                (draft.nullable !== currentColumn.nullable ||
                  (currentColumn.type === "varchar" &&
                    draft.length !== currentColumn.length) ||
                  (currentColumn.type === "decimal" &&
                    (draft.precision !== currentColumn.precision ||
                      draft.scale !== currentColumn.scale)));
              if (!editing || (schemaDirty && !blocked))
                result = await api<Schema>(
                  `${base}/tables/${encodeURIComponent(table)}/${editing ? "modify-column" : "columns"}`,
                  "POST",
                  { ...draft, version: schema.version },
                );
              const savedField = fieldDraft.widget === "lookup" && !editing
                ? { ...fieldDraft, lookup: { table: draft.relatedTable, keyColumn: draft.relatedKey, displayColumn: draft.displayColumn, searchColumns: [] } }
                : fieldDraft;
              const nextFields = canonicalObjectFields(editing
                ? objectFields.map((field) => field.name === savedField.name ? savedField : field)
                : [...objectFields, savedField]);
              const nextDataTypes = reconcileDataTypes(
                dataTypes,
                nextFields,
                maskEligibleFields(nextFields, result),
              );
              await api(
                `/admin/connections/${connection}/tables/${encodeURIComponent(table)}/object`,
                "PUT",
                {
                  fields: nextFields,
                  dataTypes: nextDataTypes,
                  defaultDataTypeKey,
                  fieldSetRules,
                  view: objectView,
                },
              );
              setSchema(result);
              setObjectFields(nextFields);
              setDataTypes(nextDataTypes);
              setDraft(null);
              setFieldDraft(null);
              setPendingVirtual(false);
              setMessage(
                editing
                  ? schemaDirty
                    ? "Field attributes and behavior updated."
                    : "Field behavior updated."
                  : draft.type === "relation"
                    ? "Relation field created. Foreign key and object lookup configured."
                    : "Field created.",
              );
            });
          }}
        >
          <div className="modal-header">
            <h2>{editing ? `Edit ${fieldDraft.name}` : "Add field"}</h2>
            <button
              type="button"
              aria-label="Close field dialog"
              onClick={closeFieldDialog}
            >
              ×
            </button>
          </div>
          <div className="form-grid">
            <label>
              Field name
              <input
                aria-label="Field name"
                required
                disabled={editing || busy}
                pattern="[A-Za-z_][A-Za-z0-9_]{0,63}"
                maxLength={64}
                value={draft.name}
                onChange={(e) => {
                  patch({ name: e.target.value });
                  patchField({ name: e.target.value, label: e.target.value });
                }}
              />
            </label>
            <label>
              Control / behavior
              <select
                aria-label="Control / behavior"
                value={fieldDraft.widget}
                disabled={busy || ["join", "formula"].includes(fieldDraft.widget)}
                onChange={(e) => {
                  const widget = e.target.value;
                  patchField({
                    widget,
                    lookup: widget === "lookup" ? fieldDraft.lookup : undefined,
                    options: widget === "dropdown" ? fieldDraft.options || [] : undefined,
                    sumup: widget === "sumup" ? fieldDraft.sumup : undefined,
                    creationDefault: widget === "sumup" ? null : fieldDraft.creationDefault,
                    readOnly: widget === "sumup" || fieldDraft.readOnly,
                  });
                  if (widget === "sumup") patch({ nullable: true });
                  if (widget === "email")
                    patch({
                      ...(!editing
                        ? {
                            type: databaseType(widget),
                            relatedTable: "",
                            relatedKey: "",
                            displayColumn: "",
                          }
                        : {}),
                      length: 255,
                    });
                  else if (!editing)
                    patch({
                      type: databaseType(widget),
                      relatedTable: "",
                      relatedKey: "",
                      displayColumn: "",
                    });
                }}
              >
                {["auto", "sumup", "join", "formula"].includes(fieldDraft.widget) && (
                  <option value={fieldDraft.widget}>
                    {fieldDraft.widget === "sumup" ? "Sum-up (stored total)" : fieldDraft.widget === "join" ? "Joined (read-only)" : fieldDraft.widget === "formula" ? "Formula (read-only)" : "Automatic"}
                  </option>
                )}
                {editing && fieldDraft.widget !== "sumup" && columns.some((column) =>
                  column.name === fieldDraft.name && ["tinyint", "smallint", "mediumint", "int", "bigint", "decimal"].includes(column.type)
                ) && <option value="sumup">Sum-up (stored total)</option>}
                {Object.entries({
                  text: "Text",
                  email: "Email",
                  textarea: "Text area",
                  number: "Number",
                  dropdown: "Dropdown",
                  checkbox: "Checkbox",
                  date: "Date",
                  datetime: "Date and time",
                  lookup: "Lookup / relation",
                }).map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </select>
            </label>
            {showDatabaseAttributes &&
              (editing ? draft.type : databaseType(fieldDraft.widget)) ===
                "text" &&
              fieldDraft.widget !== "email" && (
                <label>
                  Text length
                  <input
                    aria-label="Text length"
                    type="number"
                    required
                    min={1}
                    max={16383}
                    value={draft.length}
                    disabled={databaseAttributesDisabled}
                    onChange={(e) => patch({ length: Number(e.target.value) })}
                  />
                </label>
              )}
            {showDatabaseAttributes &&
              (editing ? draft.type : databaseType(fieldDraft.widget)) ===
                "decimal" && (
                <>
                  <label>
                    Total digits (precision)
                    <input
                      aria-label="Total digits"
                      type="number"
                      required
                      min={1}
                      max={65}
                      value={draft.precision}
                      disabled={databaseAttributesDisabled}
                      onChange={(e) =>
                        patch({ precision: Number(e.target.value) })
                      }
                    />
                  </label>
                  <label>
                    Decimal places
                    <input
                      aria-label="Decimal places"
                      type="number"
                      required
                      min={0}
                      max={Math.min(30, draft.precision)}
                      value={draft.scale}
                      disabled={databaseAttributesDisabled}
                      onChange={(e) => patch({ scale: Number(e.target.value) })}
                    />
                  </label>
                </>
              )}
          </div>
          <div className="form-grid">
            <label className="check">
              <input
                type="checkbox"
                aria-label={`${fieldDraft.name} readOnly`}
                checked={fieldDraft.readOnly}
                disabled={busy || ["join", "formula", "sumup"].includes(fieldDraft.widget)}
                onChange={(e) => {
                  patchField({ readOnly: e.target.checked });
                }}
              />
              Read-only
            </label>
          </div>
          {!['join', 'formula'].includes(fieldDraft.widget) && (
            <CreationDefaultEditor
              field={{
                ...fieldDraft,
                section: "",
                order: 0,
                hidden: false,
                showInList: true,
                required: dataTypes
                  .find((dataType) => dataType.key === defaultDataTypeKey)
                  ?.fields.find((setting) => setting.name === fieldDraft.name)?.required,
                enabledOptionKeys: (() => {
                  const setting = dataTypes
                    .find((dataType) => dataType.key === defaultDataTypeKey)
                    ?.fields.find((item) => item.name === fieldDraft.name);
                  return setting?.overrideDropdownOptions ? setting.enabledOptionKeys : null;
                })(),
              }}
              column={draftColumn}
              change={(creationDefault) => patchField({ creationDefault })}
            />
          )}
          {fieldDraft.widget === "dropdown" && (
            <DropdownConfiguration
              name={fieldDraft.name}
              options={fieldDraft.options || []}
              change={(options) => patchField({ options })}
            />
          )}
          {fieldDraft.widget === "formula" && (
            <div className="lookup-config formula-config">
              <FormulaValidator connection={connection} table={table} field={{ ...fieldDraft, section: "", order: 0, hidden: false }} fields={runtimeFields} />
              <label>Expression<textarea aria-label={`${fieldDraft.name} expression`} maxLength={1024} value={fieldDraft.formula || ""} onChange={(e) => patchField({ formula: e.target.value })} /></label>
            </div>
          )}
          {fieldDraft.widget === "join" && (
            <JoinConfiguration
              field={{ ...fieldDraft, section: "", order: 0, hidden: false }}
              connection={connection}
              tables={tables}
              sources={objectFields.filter((item) => !["join", "formula"].includes(item.widget)).map((item) => item.name)}
              change={(join) => patchField({ join })}
            />
          )}
          {fieldDraft.widget === "lookup" && editing && (
            <LookupConfiguration
              name={fieldDraft.name}
              connection={connection}
              tables={tables}
              destinations={columns}
              value={fieldDraft.lookup}
              change={(lookup) => patchField({ lookup })}
            />
          )}
          {fieldDraft.widget === "sumup" && (
            <SumupConfiguration name={fieldDraft.name} connection={connection} table={table} value={fieldDraft.sumup} change={(sumup) => patchField({ sumup })} />
          )}
          {!editing && databaseType(fieldDraft.widget) === "relation" && (
            <>
              <div className="form-grid">
                <label>
                  Related table
                  <select
                    aria-label="Related table"
                    required
                    disabled={busy}
                    value={draft.relatedTable}
                    onChange={(e) =>
                      patch({
                        relatedTable: e.target.value,
                        relatedKey: "",
                        displayColumn: "",
                      })
                    }
                  >
                    <option value="">Select table</option>
                    {tables.map((t) => (
                      <option key={t}>{t}</option>
                    ))}
                  </select>
                </label>
                <label>
                  Related key
                  <select
                    aria-label="Related key"
                    required
                    disabled={busy || !target}
                    value={draft.relatedKey}
                    onChange={(e) => patch({ relatedKey: e.target.value })}
                  >
                    <option value="">Select unique key</option>
                    {target?.columns
                      .filter(
                        (c) =>
                          c.uniqueKey &&
                          !c.nullable &&
                          !c.generated &&
                          [
                            "bigint",
                            "int",
                            "smallint",
                            "mediumint",
                            "tinyint",
                            "varchar",
                            "char",
                          ].includes(c.type),
                      )
                      .map((c) => (
                        <option key={c.name}>{c.name}</option>
                      ))}
                  </select>
                </label>
                <label>
                  Display column
                  <select
                    aria-label="Relation display column"
                    required
                    disabled={busy || !target}
                    value={draft.displayColumn}
                    onChange={(e) => patch({ displayColumn: e.target.value })}
                  >
                    <option value="">Select display value</option>
                    {target?.columns
                      .filter(
                        (c) =>
                          !["binary", "varbinary", "geometry"].includes(
                            c.type,
                          ) && !c.type.includes("blob"),
                      )
                      .map((c) => (
                        <option key={c.name}>{c.name}</option>
                      ))}
                  </select>
                </label>
              </div>
              <p className="notice">
                The key type is copied automatically. A foreign key prevents
                missing references and deletion of referenced records. A
                searchable lookup is added to the object definition.
              </p>
            </>
          )}
          {editing && (
            <p className="muted">
              Names and types remain unchanged. Defaults, indexes and comments
              are preserved. Decimal places and integer capacity cannot be
              reduced.
            </p>
          )}
          {!editing && (
            <p className="muted">
              Field requirements and input masks are configured separately for
              each data type.
            </p>
          )}
          <div className="form-actions">
            <button
              type="button"
              disabled={busy}
              onClick={closeFieldDialog}
            >
              Cancel
            </button>
            <button
              className="primary"
              disabled={
                busy ||
                (fieldDraft.widget === "dropdown" &&
                  !!dropdownError(fieldDraft.options || []))
              }
            >
              {busy ? "Applying…" : editing ? "Save field" : "Create field"}
            </button>
          </div>
        </form>
        </div>
      )}
      {schema && !creating && (
        <section
          className="card object-definition"
          aria-label="Object definition"
        >
          <div className="card-title">
            <div>
              <h2>Application object</h2>
              <p className="muted">
                Define validation, controls, relationships, defaults, formulas,
                joins and aggregates alongside their database definitions.
                Labels, sections, order and visibility belong to Layout editor.
              </p>
            </div>
          </div>
          {objectLoading ? (
            <p role="status">Loading object definition…</p>
          ) : (
            <>
              <ListViewEditor
                value={objectView}
                columns={columns}
                fields={runtimeFields}
                change={setObjectView}
              />
              <div className="table-scroll">
                <table>
                  <thead>
                    <tr>
                      <th>Field</th>
                      <th>Database definition</th>
                      <th>Control / behavior</th>
                      <th>Read-only</th>
                      <th>Creation default</th>
                      <th>Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    {dataTypes.length > 0 && (() => {
                      const managedColumn = schema.columns.find(
                        (column) => column.name === "datatype",
                      );
                      return (
                        <tr key="datatype">
                          <td>
                            datatype <span className="badge">Managed</span>
                          </td>
                          <td>
                            <strong>{managedColumn?.sqlType || "varchar(64)"}</strong>
                            <small className="muted database-field-details">
                              {managedColumn
                                ? "Required in database"
                                : "Provisioned when object settings are saved"}
                            </small>
                          </td>
                          <td>Data type</td>
                          <td>Yes</td>
                          <td>
                            <select
                              aria-label="datatype default value"
                              value={defaultDataTypeKey}
                              disabled={busy || objectLoading}
                              onChange={(event) =>
                                void saveDataTypes(dataTypes, event.target.value)
                              }
                            >
                              {dataTypes.map((dataType) => (
                                <option key={dataType.key} value={dataType.key}>
                                  {dataType.label}
                                </option>
                              ))}
                            </select>
                          </td>
                          <td>
                            <small className="muted">Managed by Data Types.</small>
                          </td>
                        </tr>
                      );
                    })()}
                    {objectFields.map((field) => {
                      const column = columns.find((c) => c.name === field.name);
                      const schemaColumn = schema.columns.find(
                        (c) => c.name === field.name,
                      );
                      const virtual = ["join", "formula"].includes(
                        field.widget,
                      );
                      return (
                        <tr key={field.name}>
                          <td>
                            {field.name}
                            {virtual && <span className="badge">Virtual</span>}
                          </td>
                          <td>
                            {schemaColumn ? (
                              <>
                                <strong>{schemaColumn.sqlType}</strong>
                                <small className="muted database-field-details">
                                  {schemaColumn.nullable
                                    ? "Allows NULL"
                                    : "Required in database"}
                                  {schemaColumn.primaryKey
                                    ? " · Primary key"
                                    : schemaColumn.relatedTable
                                      ? ` · ${schemaColumn.relatedTable}.${schemaColumn.relatedKey}`
                                      : schemaColumn.uniqueKey
                                        ? " · Unique key"
                                        : ""}
                                </small>
                              </>
                            ) : (
                              "Application-only"
                            )}
                          </td>
                          <td>
                            {field.widget === "join" ? "Joined (read-only)" : field.widget === "formula" ? "Formula (read-only)" : field.widget}
                          </td>
                          <td>
                            {field.readOnly ? "Yes" : "No"}
                          </td>
                          <td>
                            {field.creationDefault ? "Configured" : "—"}
                          </td>
                          <td>
                            <div className="actions">
                                <button
                                  disabled={busy || field.name === "datatype"}
                                  title={schemaColumn?.editBlocked || "Edit field"}
                                  aria-label={`Edit field ${field.name}`}
                                  onClick={() => {
                                    setDraft({
                                      ...blank(),
                                      name: field.name,
                                      type: schemaColumn ? kind(schemaColumn) : "text",
                                      nullable: schemaColumn?.nullable ?? true,
                                      length: schemaColumn?.length || 255,
                                      precision: schemaColumn?.precision || 18,
                                      scale: schemaColumn?.scale ?? 2,
                                    });
                                    setFieldDraft({ ...field });
                                    setPendingVirtual(false);
                                    setEditing(true);
                                    setError("");
                                    setMessage("");
                                  }}
                                >
                                  Edit
                                </button>
                                <button
                                  className="danger"
                                  aria-label={`Delete field ${field.name}`}
                                  disabled={busy || field.name === "datatype" || !!schemaColumn?.primaryKey || !!schemaColumn?.autoIncrement}
                                  onClick={() => {
                                    if (!window.confirm(`Delete field ${field.name}? This permanently removes the field and its configuration.`)) return;
                                    void run(async () => {
                                      await api(`/admin/connections/${connection}/tables/${encodeURIComponent(table)}/fields/${encodeURIComponent(field.name)}`, "DELETE");
                                      setMessage(`Field ${field.name} deleted.`);
                                    });
                                  }}
                                >Delete</button>
                                {schemaColumn?.editBlocked && (
                                  <small className="muted">
                                    {" "}
                                    {schemaColumn.editBlocked}
                                  </small>
                                )}
                            </div>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
              <div className="object-editor-footer">
                <div className="actions object-field-actions">
                  <button
                    type="button"
                    className="primary"
                    disabled={busy}
                    onClick={() => {
                      setDraft(blank());
                      setFieldDraft({
                        name: "",
                        label: "",
                        readOnly: false,
                        widget: "text",
                      });
                      setPendingVirtual(false);
                      setEditing(false);
                      setError("");
                      setMessage("");
                    }}
                  >
                    Add field
                  </button>
                  <button
                    type="button"
                    disabled={
                      objectFields.filter((f) =>
                        ["join", "formula"].includes(f.widget),
                      ).length >= 20
                    }
                    onClick={() => {
                      let n = 1;
                      while (objectFields.some((f) => f.name === `joined_${n}`))
                        n++;
                      const added: ObjectField = {
                        name: `joined_${n}`,
                        label: "Related value",
                        readOnly: true,
                        widget: "join",
                        join: {
                          sourceColumn: "",
                          table: "",
                          keyColumn: "",
                          valueColumn: "",
                        },
                      };
                      setObjectFields((old) => [...old, added]);
                      setFieldDraft(added);
                      setDraft({ ...blank(), name: added.name });
                      setPendingVirtual(true);
                      setEditing(true);
                    }}
                  >
                    Add joined field
                  </button>
                  <button
                    type="button"
                    disabled={
                      objectFields.filter((f) =>
                        ["join", "formula"].includes(f.widget),
                      ).length >= 20
                    }
                    onClick={() => {
                      let n = 1;
                      while (
                        objectFields.some((f) => f.name === `formula_${n}`)
                      )
                        n++;
                      const added: ObjectField = {
                        name: `formula_${n}`,
                        label: "Calculated value",
                        readOnly: true,
                        widget: "formula",
                        formula: "",
                      };
                      setObjectFields((old) => [...old, added]);
                      setFieldDraft(added);
                      setDraft({ ...blank(), name: added.name });
                      setPendingVirtual(true);
                      setEditing(true);
                    }}
                  >
                    Add formula field
                  </button>
                </div>
                <div className="actions object-save-actions">
                  {objectFields.some((field) => field.widget === "sumup") && (
                    <button
                      disabled={busy || objectLoading}
                      onClick={() =>
                        void run(async () => {
                          await api(
                            `/admin/connections/${connection}/tables/${encodeURIComponent(table)}/sumups/recalculate`,
                            "POST",
                          );
                          setMessage(
                            "Sum-ups recalculated from all existing child records.",
                          );
                        })
                      }
                    >
                      Recalculate saved sum-ups
                    </button>
                  )}
                  <button
                    className="primary"
                    disabled={
                      busy ||
                      objectLoading ||
                      !table ||
                      objectFields.length === 0 ||
                      objectFields.some(
                        (field) =>
                          field.widget === "dropdown" &&
                          !!dropdownError(field.options || []),
                      )
                    }
                    onClick={() =>
                      void run(async () => {
                        await api(
                          `/admin/connections/${connection}/tables/${encodeURIComponent(table)}/object`,
                          "PUT",
                          {
                            fields: canonicalObjectFields(objectFields),
                            dataTypes: reconcileDataTypes(
                              dataTypes,
                              objectFields,
                              maskEligibleFields(objectFields, schema),
                            ),
                            defaultDataTypeKey,
                            fieldSetRules,
                            view: {
                              ...objectView,
                              label: objectView.label?.trim(),
                            },
                          },
                        );
                        setMessage(
                          "Object saved. Application behavior updated.",
                        );
                      })
                    }
                  >
                    Save object
                  </button>
                </div>
              </div>
              <FieldSetRulesEditor
                key={`${connection}:${table}`}
                value={fieldSetRules}
                fields={fieldSetCandidates}
                runtimeFields={runtimeFields}
                dataTypes={dataTypes}
                connection={connection}
                table={table}
                disabled={busy || objectLoading}
                change={setFieldSetRules}
                save={saveFieldSetRules}
              />
              <DataTypesEditor
                dataTypes={dataTypes}
                defaultDataTypeKey={defaultDataTypeKey}
                fields={objectFields}
                maskEligibleFieldNames={maskEligibleFields(objectFields, schema)}
                disabled={busy || objectLoading}
                save={saveDataTypes}
              />
            </>
          )}
        </section>
      )}
    </div>
  );
}

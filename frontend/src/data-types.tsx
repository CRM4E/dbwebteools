import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import type { DataTypeDefinition, DataTypeField, InputMask, ObjectField } from "./api";
import { maskConfigurationError, maskTip } from "./field-mask";

const emptySetting = (name: string): DataTypeField => ({
  name,
  required: false,
  mask: null,
  overrideDropdownOptions: false,
  enabledOptionKeys: [],
});

export function reconcileDataTypes(
  dataTypes: DataTypeDefinition[],
  fields: ObjectField[],
  maskEligibleFieldNames?: ReadonlySet<string>,
): DataTypeDefinition[] {
  const names = new Set(fields.map((field) => field.name));
  return dataTypes.map((dataType) => ({
    ...dataType,
    fields: fields.map((field) => {
      const existing = dataType.fields?.find((setting) => setting.name === field.name);
      const setting = existing ? { ...emptySetting(field.name), ...existing } : emptySetting(field.name);
      if (field.widget !== "dropdown") {
        setting.overrideDropdownOptions = false;
        setting.enabledOptionKeys = [];
      } else if (setting.overrideDropdownOptions) {
        const keys = new Set((field.options || []).map((option) => option.key));
        setting.enabledOptionKeys = setting.enabledOptionKeys.filter((key) => keys.has(key));
      } else {
        setting.enabledOptionKeys = [];
      }
      const maskEligible = maskEligibleFieldNames
        ? maskEligibleFieldNames.has(field.name)
        : ["text", "textarea"].includes(field.widget);
      if (!maskEligible) setting.mask = null;
      if (field.readOnly || ["join", "formula", "sumup"].includes(field.widget)) {
        setting.required = false;
        setting.mask = null;
      }
      return setting;
    }).filter((setting) => names.has(setting.name)),
  }));
}

export function normalizeDataTypes(
  fields: ObjectField[],
  dataTypes?: DataTypeDefinition[] | null,
  defaultDataTypeKey?: string | null,
  maskEligibleFieldNames?: ReadonlySet<string>,
) {
  const supplied = (dataTypes || []).filter((dataType) => dataType.key);
  const normalized = reconcileDataTypes(
    supplied.length
      ? supplied
      : [{
          key: "default",
          label: "Default",
          fields: fields.map((field) => ({
            ...emptySetting(field.name),
            required: !!field.required,
            mask: field.mask || null,
          })),
        }],
    fields,
    maskEligibleFieldNames,
  );
  const requested = normalized.some((dataType) => dataType.key === defaultDataTypeKey)
    ? defaultDataTypeKey!
    : normalized[0].key;
  return { dataTypes: normalized, defaultDataTypeKey: requested };
}

type FieldDraft = { dataTypeKey: string; setting: DataTypeField };
type TypeDraft = { originalKey?: string; key: string; label: string };

function dialogKeyDown(
  event: KeyboardEvent,
  dialog: HTMLFormElement | null,
  close: () => void,
) {
  if (event.key === "Escape") {
    event.preventDefault();
    close();
    return;
  }
  if (event.key !== "Tab" || !dialog) return;
  const controls = dialog.querySelectorAll<HTMLElement>(
    "button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled)",
  );
  if (!controls.length) return;
  const first = controls[0], last = controls[controls.length - 1];
  if (event.shiftKey && document.activeElement === first) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault();
    first.focus();
  }
}

export function DataTypesEditor({
  dataTypes,
  defaultDataTypeKey,
  fields,
  maskEligibleFieldNames,
  disabled,
  save,
}: {
  dataTypes: DataTypeDefinition[];
  defaultDataTypeKey: string;
  fields: ObjectField[];
  maskEligibleFieldNames?: ReadonlySet<string>;
  disabled?: boolean;
  save: (dataTypes: DataTypeDefinition[], defaultDataTypeKey: string) => Promise<void>;
}) {
  const [selectedKey, setSelectedKey] = useState(defaultDataTypeKey);
  const [typeDraft, setTypeDraft] = useState<TypeDraft | null>(null);
  const [fieldDraft, setFieldDraft] = useState<FieldDraft | null>(null);
  const [error, setError] = useState("");
  const [saving, setSaving] = useState(false);
  const typeDialogRef = useRef<HTMLFormElement>(null);
  const fieldDialogRef = useRef<HTMLFormElement>(null);
  useEffect(() => {
    if (!dataTypes.some((dataType) => dataType.key === selectedKey))
      setSelectedKey(defaultDataTypeKey || dataTypes[0]?.key || "");
  }, [dataTypes, defaultDataTypeKey, selectedKey]);
  useEffect(() => {
    if (!typeDraft) return;
    const previous = document.activeElement as HTMLElement | null;
    typeDialogRef.current?.querySelector<HTMLElement>("input:not(:disabled)")?.focus();
    return () => previous?.focus();
  }, [Boolean(typeDraft)]);
  useEffect(() => {
    if (!fieldDraft) return;
    const previous = document.activeElement as HTMLElement | null;
    fieldDialogRef.current
      ?.querySelector<HTMLElement>("input:not(:disabled),button:not(:disabled)")
      ?.focus();
    return () => previous?.focus();
  }, [Boolean(fieldDraft)]);
  const selected = dataTypes.find((dataType) => dataType.key === selectedKey) || dataTypes[0];
  const duplicateTypeKey = !!typeDraft && !typeDraft.originalKey && dataTypes.some(
    (item) => item.key.toLowerCase() === typeDraft.key.trim().toLowerCase(),
  );
  const duplicateTypeLabel = !!typeDraft && dataTypes.some(
    (item) =>
      item.key !== typeDraft.originalKey &&
      item.label.toLowerCase() === typeDraft.label.trim().toLowerCase(),
  );

  async function persist(next: DataTypeDefinition[], defaultKey: string) {
    setSaving(true);
    setError("");
    try {
      await save(reconcileDataTypes(next, fields, maskEligibleFieldNames), defaultKey);
      return true;
    } catch (reason) {
      setError((reason as Error).message);
      return false;
    } finally {
      setSaving(false);
    }
  }

  const patchSetting = (patch: Partial<DataTypeField>) =>
    setFieldDraft((old) => old ? { ...old, setting: { ...old.setting, ...patch } } : old);
  const mask = fieldDraft?.setting.mask;
  const maskError = mask ? maskConfigurationError(mask) : null;
  const legacyMask = !!mask && mask.pattern == null;
  const configuredField = fields.find((field) => field.name === fieldDraft?.setting.name);

  return (
    <section className="data-types" aria-label="Data types">
      <div className="card-title">
        <div>
          <h2>Data Types</h2>
          <p className="muted">Each record uses an immutable type key. The default type controls field validation and dropdown choices.</p>
        </div>
        <button
          type="button"
          className="primary"
          disabled={disabled || saving || dataTypes.length >= 100}
          onClick={() => setTypeDraft({ key: "", label: "" })}
        >Add data type</button>
      </div>
      {error && <p className="alert" role="alert">{error}</p>}
      <div className="data-type-layout">
        <div className="table-scroll">
          <table>
            <thead><tr><th>Label</th><th>Key</th><th>Default</th><th>Actions</th></tr></thead>
            <tbody>
              {dataTypes.map((dataType) => (
                <tr key={dataType.key} className={dataType.key === selected?.key ? "selected-row" : undefined}>
                  <td><button type="button" className="link-button" onClick={() => setSelectedKey(dataType.key)}>{dataType.label}</button></td>
                  <td><code>{dataType.key}</code></td>
                  <td>{dataType.key === defaultDataTypeKey ? <span className="badge">Default</span> : <button type="button" aria-label={`Set ${dataType.label} as default`} disabled={disabled || saving} onClick={() => void persist(dataTypes, dataType.key)}>Set as default</button>}</td>
                  <td><div className="actions">
                    <button type="button" aria-label={`Edit data type ${dataType.label}`} disabled={disabled || saving} onClick={() => setTypeDraft({ originalKey: dataType.key, key: dataType.key, label: dataType.label })}>Edit</button>
                    <button type="button" className="danger" aria-label={`Delete data type ${dataType.label}`} disabled={disabled || saving || dataTypes.length === 1 || dataType.key === defaultDataTypeKey} onClick={() => {
                      if (!window.confirm(`Delete data type ${dataType.label}?`)) return;
                      const next = dataTypes.filter((item) => item.key !== dataType.key);
                      setSelectedKey(defaultDataTypeKey);
                      void persist(next, defaultDataTypeKey);
                    }}>Delete</button>
                  </div></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {selected && (
          <div className="data-type-fields">
            <h3>{selected.label} fields</h3>
            <div className="table-scroll"><table>
              <thead><tr><th>Field</th><th>Required</th><th>Input mask</th><th>Dropdown values</th><th>Actions</th></tr></thead>
              <tbody>{fields.filter((field) => field.name !== "datatype").map((field) => {
                const setting = selected.fields.find((item) => item.name === field.name) || emptySetting(field.name);
                return <tr key={field.name}>
                  <td>{field.label || field.name}<small className="muted database-field-details">{field.name}</small></td>
                  <td>{setting.required ? "Yes" : "No"}</td>
                  <td>{setting.mask ? maskTip(setting.mask) : "—"}</td>
                  <td>{field.widget !== "dropdown" ? "—" : setting.overrideDropdownOptions ? `${setting.enabledOptionKeys.length} enabled` : "All values"}</td>
                  <td><button type="button" aria-label={`Configure ${field.name} for ${selected.label}`} disabled={disabled || saving || field.readOnly || ["join", "formula", "sumup"].includes(field.widget)} onClick={() => setFieldDraft({ dataTypeKey: selected.key, setting: { ...setting, enabledOptionKeys: [...setting.enabledOptionKeys] } })}>Configure</button></td>
                </tr>;
              })}</tbody>
            </table></div>
          </div>
        )}
      </div>
      {typeDraft && <div className="overlay" role="presentation" onKeyDown={(event) => dialogKeyDown(event, typeDialogRef.current, () => setTypeDraft(null))}><form ref={typeDialogRef} className="modal" role="dialog" aria-modal="true" aria-label={typeDraft.originalKey ? "Edit data type" : "Add data type"} onSubmit={(event) => {
        event.preventDefault();
        const key = typeDraft.key.trim();
        const label = typeDraft.label.trim();
        const next = typeDraft.originalKey
          ? dataTypes.map((item) => item.key === typeDraft.originalKey ? { ...item, label } : item)
          : [...dataTypes, { key, label, fields: fields.filter((field) => field.name !== "datatype").map((field) => emptySetting(field.name)) }];
        void persist(next, defaultDataTypeKey || key).then((saved) => {
          if (saved) { setSelectedKey(key); setTypeDraft(null); }
        });
      }}>
        <div className="modal-header"><h2>{typeDraft.originalKey ? "Edit data type" : "Add data type"}</h2><button type="button" aria-label="Close data type dialog" onClick={() => setTypeDraft(null)}>×</button></div>
        <div className="form-grid"><label>Key<input aria-label="Data type key" required disabled={!!typeDraft.originalKey || saving} maxLength={64} value={typeDraft.key} onChange={(event) => setTypeDraft({ ...typeDraft, key: event.target.value })} /></label><label>Label<input aria-label="Data type label" required maxLength={150} disabled={saving} value={typeDraft.label} onChange={(event) => setTypeDraft({ ...typeDraft, label: event.target.value })} /></label></div>
        {(duplicateTypeKey || duplicateTypeLabel) && <p className="alert" role="alert">Data type keys and labels must each be unique.</p>}
        <div className="form-actions"><button type="button" disabled={saving} onClick={() => setTypeDraft(null)}>Cancel</button><button className="primary" disabled={saving || duplicateTypeKey || duplicateTypeLabel}>Save data type</button></div>
      </form></div>}
      {fieldDraft && configuredField && <div className="overlay" role="presentation" onKeyDown={(event) => dialogKeyDown(event, fieldDialogRef.current, () => setFieldDraft(null))}><form ref={fieldDialogRef} className="modal" role="dialog" aria-modal="true" aria-label="Data type field settings" onSubmit={(event) => {
        event.preventDefault();
        const next = dataTypes.map((dataType) => dataType.key !== fieldDraft.dataTypeKey ? dataType : { ...dataType, fields: dataType.fields.map((setting) => setting.name === fieldDraft.setting.name ? fieldDraft.setting : setting) });
        void persist(next, defaultDataTypeKey).then((saved) => {
          if (saved) setFieldDraft(null);
        });
      }}>
        <div className="modal-header"><h2>{configuredField.label || configuredField.name} settings</h2><button type="button" aria-label="Close field settings dialog" onClick={() => setFieldDraft(null)}>×</button></div>
        <label className="check"><input type="checkbox" aria-label="Required" checked={fieldDraft.setting.required} disabled={saving} onChange={(event) => patchSetting({ required: event.target.checked })} />Required</label>
        {(maskEligibleFieldNames
          ? maskEligibleFieldNames.has(configuredField.name)
          : ["text", "textarea"].includes(configuredField.widget)) && (
          <div className="lookup-config" aria-label="Input mask configuration">
            <h3>Input mask</h3>
            <div className="form-grid">
              <label>
                Regex-style mask pattern
                <input
                  aria-label="Regex-style mask pattern"
                  aria-describedby="data-type-mask-help data-type-mask-status"
                  maxLength={1024}
                  disabled={saving}
                  placeholder="For example: AA-###"
                  aria-invalid={!!maskError}
                  value={mask?.pattern || ""}
                  onChange={(event) =>
                    patchSetting({ mask: event.target.value ? { pattern: event.target.value } : null })
                  }
                />
              </label>
            </div>
            <p id="data-type-mask-help" className="muted">
              Use # for an ASCII digit, A for a letter, and X for a letter or digit.
              Add ? after a position to make it optional. Example: ### accepts exactly
              three digits; AA-### accepts two letters, a dash, and three digits.
            </p>
            {legacyMask && (
              <p className="muted">
                Existing legacy rule: {maskTip(mask!)} Replace it by entering a pattern
                or remove it.
              </p>
            )}
            <p
              id="data-type-mask-status"
              className={maskError ? "alert" : "muted"}
              role={maskError ? "alert" : undefined}
            >
              {maskError || (mask ? `User tip: ${maskTip(mask)}` : "No input mask.")}
            </p>
            {mask && (
              <button type="button" disabled={saving} onClick={() => patchSetting({ mask: null })}>
                Remove input mask
              </button>
            )}
          </div>
        )}
        {configuredField.widget === "dropdown" && <fieldset className="dropdown-config"><legend>Dropdown values</legend><label className="check"><input type="checkbox" aria-label="Override dropdown options" checked={fieldDraft.setting.overrideDropdownOptions} disabled={saving} onChange={(event) => patchSetting({ overrideDropdownOptions: event.target.checked, enabledOptionKeys: [] })} />Override dropdown options</label>{fieldDraft.setting.overrideDropdownOptions && <div className="option-checklist">{(configuredField.options || []).map((option) => <label className="check" key={option.key}><input type="checkbox" aria-label={`Enable ${option.display}`} checked={fieldDraft.setting.enabledOptionKeys.includes(option.key)} disabled={saving} onChange={(event) => patchSetting({ enabledOptionKeys: event.target.checked ? [...fieldDraft.setting.enabledOptionKeys, option.key] : fieldDraft.setting.enabledOptionKeys.filter((key) => key !== option.key) })} />{option.display} <code>{option.key}</code></label>)}</div>}</fieldset>}
        <div className="form-actions"><button type="button" disabled={saving} onClick={() => setFieldDraft(null)}>Cancel</button><button className="primary" disabled={saving || !!maskError}>Save field settings</button></div>
      </form></div>}
    </section>
  );
}

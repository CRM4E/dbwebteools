import { useState } from "react";
import type { FieldSetRule } from "./api";

type Candidate = { name: string; label: string };

export function FieldSetRulesEditor({
  value,
  fields,
  disabled,
  change,
}: {
  value: FieldSetRule[];
  fields: Candidate[];
  disabled?: boolean;
  change: (rules: FieldSetRule[]) => void;
}) {
  const [draft, setDraft] = useState<(FieldSetRule & { index: number | null }) | null>(null);
  const open = (rule?: FieldSetRule, index: number | null = null) =>
    setDraft({
      field: rule?.field || fields[0]?.name || "",
      condition: rule?.condition || "",
      value: rule?.value || "",
      index,
    });
  const complete = !!draft?.field && !!draft.condition.trim() && !!draft.value.trim();
  return (
    <section className="field-set-rules" aria-label="Field set rules">
      <div className="card-title">
        <div>
          <h3>Field set rules</h3>
          <p className="muted">
            Rules run from top to bottom after field validation and before the record is saved.
            Later rules can use values set by earlier rules.
          </p>
        </div>
        <button type="button" disabled={disabled || value.length >= 50 || fields.length === 0} onClick={() => open()}>
          Add set rule
        </button>
      </div>
      <div className="table-scroll">
        <table>
          <thead><tr><th>Field to set</th><th>Condition formula</th><th>Value formula</th><th>Actions</th></tr></thead>
          <tbody>
            {value.length === 0 && <tr><td colSpan={4} className="muted">No field set rules.</td></tr>}
            {value.map((rule, index) => (
              <tr key={`${index}-${rule.field}`}>
                <td>{fields.find((field) => field.name === rule.field)?.label || rule.field}<small className="muted database-field-details">{rule.field}</small></td>
                <td><code>{rule.condition}</code></td>
                <td><code>{rule.value}</code></td>
                <td><div className="actions">
                  <button type="button" disabled={disabled} aria-label={`Edit set rule ${index + 1}`} onClick={() => open(rule, index)}>Edit</button>
                  <button type="button" disabled={disabled || index === 0} aria-label={`Move set rule ${index + 1} up`} onClick={() => { const next = [...value]; [next[index - 1], next[index]] = [next[index], next[index - 1]]; change(next); }}>↑</button>
                  <button type="button" disabled={disabled || index === value.length - 1} aria-label={`Move set rule ${index + 1} down`} onClick={() => { const next = [...value]; [next[index], next[index + 1]] = [next[index + 1], next[index]]; change(next); }}>↓</button>
                  <button type="button" className="danger" disabled={disabled} aria-label={`Delete set rule ${index + 1}`} onClick={() => change(value.filter((_, candidate) => candidate !== index))}>Delete</button>
                </div></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {draft && (
        <div className="inline-editor" role="dialog" aria-label={draft.index == null ? "Add field set rule" : "Edit field set rule"}>
          <div className="form-grid">
            <label>Field to set<select aria-label="Set rule field" value={draft.field} disabled={disabled} onChange={(event) => setDraft({ ...draft, field: event.target.value })}>
              {fields.map((field) => <option key={field.name} value={field.name}>{field.label} · {field.name}</option>)}
            </select></label>
            <label>Condition formula<textarea aria-label="Set rule condition formula" maxLength={1024} required value={draft.condition} disabled={disabled} onChange={(event) => setDraft({ ...draft, condition: event.target.value })} /></label>
            <label>Value formula<textarea aria-label="Set rule value formula" maxLength={1024} required value={draft.value} disabled={disabled} onChange={(event) => setDraft({ ...draft, value: event.target.value })} /></label>
          </div>
          <p className="muted">Use stored fields as <code>[field_name]</code>. Conditions must return true or false; values use the same formula functions as calculated fields.</p>
          <div className="form-actions">
            <button type="button" disabled={disabled} onClick={() => setDraft(null)}>Cancel</button>
            <button type="button" className="primary" disabled={disabled || !complete} onClick={() => {
              if (!complete) return;
              const rule = { field: draft.field, condition: draft.condition.trim(), value: draft.value.trim() };
              change(draft.index == null ? [...value, rule] : value.map((candidate, index) => index === draft.index ? rule : candidate));
              setDraft(null);
            }}>Save set rule</button>
          </div>
        </div>
      )}
    </section>
  );
}

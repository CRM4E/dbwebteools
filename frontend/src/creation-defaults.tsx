import { type Column, type Field } from "./api";

export function CreationDefaultEditor({
  field,
  column,
  change,
}: {
  field: Field;
  column?: Column;
  change: (value: Field["creationDefault"]) => void;
}) {
  if (
    !column ||
    column.generated ||
    column.autoIncrement ||
    ["sumup", "join", "formula"].includes(field.widget)
  )
    return <small>Calculated automatically</small>;
  if (
    ![
      "tinyint",
      "smallint",
      "mediumint",
      "int",
      "bigint",
      "decimal",
      "float",
      "double",
      "bit",
      "year",
      "char",
      "varchar",
      "tinytext",
      "text",
      "mediumtext",
      "longtext",
      "enum",
      "set",
      "date",
      "datetime",
      "timestamp",
    ].includes(column.type)
  )
    return <small>Use database default</small>;
  const setting = field.creationDefault;
  return (
    <div className="creation-default">
      <select
        aria-label={`${field.name} default mode`}
        value={!setting ? "none" : setting.isNull ? "null" : "value"}
        onChange={(e) =>
          change(
            e.target.value === "none"
              ? null
              : e.target.value === "null"
                ? { isNull: true }
                : {
                    value: field.widget === "checkbox" ? "false" : "",
                    isNull: false,
                  },
          )
        }
      >
        <option value="none">Database default</option>
        <option value="value">Value</option>
        {column.nullable && !field.required && (
          <option value="null">NULL</option>
        )}
      </select>
      {setting &&
        !setting.isNull &&
        (field.widget === "dropdown" ? (
          <select
            aria-label={`${field.name} default value`}
            value={setting.value ?? ""}
            onChange={(e) => change({ value: e.target.value })}
          >
            <option value="">Choose value…</option>
            {field.options
              ?.filter((option) =>
                field.enabledOptionKeys == null
                  ? true
                  : field.enabledOptionKeys.includes(option.key) ||
                    option.key === setting.value,
              )
              .map((o) => (
              <option
                key={o.key}
                value={o.key}
                disabled={
                  field.enabledOptionKeys != null &&
                  !field.enabledOptionKeys.includes(o.key)
                }
              >
                {o.display} ({o.key})
                {field.enabledOptionKeys != null &&
                !field.enabledOptionKeys.includes(o.key)
                  ? " — not available"
                  : ""}
              </option>
            ))}
          </select>
        ) : field.widget === "checkbox" ? (
          <select
            aria-label={`${field.name} default value`}
            value={setting.value ?? "false"}
            onChange={(e) => change({ value: e.target.value })}
          >
            <option value="false">False (0)</option>
            <option value="true">True (1)</option>
          </select>
        ) : (
          <input
            aria-label={`${field.name} default value`}
            maxLength={4000}
            type={
              column.type === "date" || field.widget === "date"
                ? "date"
                : ["datetime", "timestamp"].includes(column.type)
                  ? "datetime-local"
                  : "text"
            }
            step="any"
            value={setting.value ?? ""}
            placeholder={
              field.widget === "lookup" ? "Stored lookup key" : "Default value"
            }
            onChange={(e) => change({ value: e.target.value })}
          />
        ))}
    </div>
  );
}

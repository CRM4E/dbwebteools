// @vitest-environment jsdom
import React from "react";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DataTypeDefinition, ObjectField } from "./api";
import { DataTypesEditor, normalizeDataTypes, reconcileDataTypes } from "./data-types";

const fields: ObjectField[] = [
  { name: "title", label: "Title", readOnly: false, widget: "text" },
  {
    name: "status",
    label: "Status",
    readOnly: false,
    widget: "dropdown",
    options: [
      { key: "draft", display: "Draft" },
      { key: "ready", display: "Ready" },
    ],
  },
];
const base: DataTypeDefinition = {
  key: "default",
  label: "Default",
  fields: fields.map((field) => ({
    name: field.name,
    required: false,
    mask: null,
    overrideDropdownOptions: false,
    enabledOptionKeys: [],
  })),
};

afterEach(cleanup);

describe("data type metadata", () => {
  it("normalizes legacy required and masks into one default type", () => {
    const result = normalizeDataTypes([
      { ...fields[0], required: true, mask: { pattern: "AA-###" } },
      fields[1],
    ]);
    expect(result.defaultDataTypeKey).toBe("default");
    expect(result.dataTypes[0].fields).toContainEqual(
      expect.objectContaining({ name: "title", required: true, mask: { pattern: "AA-###" } }),
    );
  });

  it("adds missing field settings and removes stale enabled dropdown keys", () => {
    const result = reconcileDataTypes([{
      ...base,
      fields: [{
        name: "status", required: true, mask: null, overrideDropdownOptions: true,
        enabledOptionKeys: ["ready", "removed"],
      }],
    }], fields);
    expect(result[0].fields).toContainEqual(expect.objectContaining({ name: "title" }));
    expect(result[0].fields.find((field) => field.name === "status")?.enabledOptionKeys).toEqual(["ready"]);
  });

  it("clears required and masks when a field becomes read-only", () => {
    const result = reconcileDataTypes(
      [{
        ...base,
        fields: [
          { name: "title", required: true, mask: { pattern: "AA" }, overrideDropdownOptions: false, enabledOptionKeys: [] },
          base.fields[1],
        ],
      }],
      [{ ...fields[0], readOnly: true }, fields[1]],
    );
    expect(result[0].fields[0]).toEqual(
      expect.objectContaining({ required: false, mask: null }),
    );
  });

  it("shows and explicitly removes a migrated custom mask", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    const custom = {
      ...base,
      fields: base.fields.map((setting) =>
        setting.name === "title"
          ? {
              ...setting,
              mask: {
                characterSet: "letters" as const,
                minimumLength: 4,
                requiredCharacters: "-",
              },
            }
          : setting,
      ),
    };
    render(
      <DataTypesEditor
        dataTypes={[custom]}
        defaultDataTypeKey="default"
        fields={fields}
        save={save}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "Configure title for Default" }));
    expect(screen.getByText(/Existing custom rule: Use at least 4 characters/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Remove input mask" }));
    fireEvent.click(screen.getByRole("button", { name: "Save field settings" }));
    await waitFor(() => expect(save).toHaveBeenCalled());
    expect(save.mock.calls[0][0][0].fields[0].mask).toBeNull();
  });

  it("focuses and closes dialogs with Escape", () => {
    render(
      <DataTypesEditor
        dataTypes={[base]}
        defaultDataTypeKey="default"
        fields={fields}
        save={vi.fn()}
      />,
    );
    const add = screen.getByRole("button", { name: "Add data type" });
    add.focus();
    fireEvent.click(add);
    expect(document.activeElement).toBe(screen.getByLabelText("Data type key"));
    fireEvent.keyDown(screen.getByRole("dialog", { name: "Add data type" }), {
      key: "Escape",
    });
    expect(screen.queryByRole("dialog", { name: "Add data type" })).toBeNull();
    expect(document.activeElement).toBe(add);
  });

  it("allows an explicit empty dropdown override and then selected master values", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    render(<DataTypesEditor dataTypes={[base]} defaultDataTypeKey="default" fields={fields} save={save} />);
    fireEvent.click(screen.getByRole("button", { name: "Configure status for Default" }));
    fireEvent.click(screen.getByLabelText("Override dropdown options"));
    expect((screen.getByLabelText("Enable Ready") as HTMLInputElement).checked).toBe(false);
    fireEvent.click(screen.getByRole("button", { name: "Save field settings" }));
    await waitFor(() => expect(save).toHaveBeenCalled());
    expect(save.mock.calls[0][0][0].fields.find((field: { name: string }) => field.name === "status")).toEqual(
      expect.objectContaining({ overrideDropdownOptions: true, enabledOptionKeys: [] }),
    );

    cleanup();
    save.mockClear();
    render(<DataTypesEditor dataTypes={[base]} defaultDataTypeKey="default" fields={fields} save={save} />);
    fireEvent.click(screen.getByRole("button", { name: "Configure status for Default" }));
    fireEvent.click(screen.getByLabelText("Override dropdown options"));
    fireEvent.click(screen.getByLabelText("Enable Ready"));
    fireEvent.click(screen.getByRole("button", { name: "Save field settings" }));
    await waitFor(() => expect(save).toHaveBeenCalled());
    expect(save.mock.calls[0][0][0].fields.find((field: { name: string }) => field.name === "status").enabledOptionKeys).toEqual(["ready"]);
  });

  it("keeps keys immutable while labels remain editable", () => {
    render(<DataTypesEditor dataTypes={[base]} defaultDataTypeKey="default" fields={fields} save={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: "Edit data type Default" }));
    expect((screen.getByLabelText("Data type key") as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText("Data type label") as HTMLInputElement).disabled).toBe(false);
  });

  it("persists exactly one default and protects it from deletion", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    const invoice = { ...base, key: "invoice", label: "Invoice" };
    render(
      <DataTypesEditor
        dataTypes={[base, invoice]}
        defaultDataTypeKey="default"
        fields={fields}
        save={save}
      />,
    );
    expect(
      (screen.getByRole("button", { name: "Delete data type Default" }) as HTMLButtonElement).disabled,
    ).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "Set Invoice as default" }));
    await waitFor(() => expect(save).toHaveBeenCalledWith(expect.any(Array), "invoice"));
  });
});

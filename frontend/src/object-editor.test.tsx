// @vitest-environment jsdom
import React from "react";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ObjectEditor } from "./object-editor";
import { api } from "./api";

vi.mock("./api", async (original) => {
  const actual = await original<typeof import("./api")>();
  return { ...actual, api: vi.fn() };
});

const schema = {
  version: "v1",
  columns: [
    { name: "id", type: "bigint", sqlType: "bigint", nullable: false, primaryKey: true, autoIncrement: true, generated: false, length: null, precision: 20, scale: 0, default: null, relatedTable: null, relatedKey: null, uniqueKey: true, editBlocked: "Primary keys are managed outside the designer." },
    { name: "title", type: "varchar", sqlType: "varchar(100)", nullable: true, primaryKey: false, autoIncrement: false, generated: false, length: 100, precision: null, scale: null, default: null, relatedTable: null, relatedKey: null, uniqueKey: false, editBlocked: null },
  ],
};
const definition = { fields: [
  { name: "id", label: "Id", readOnly: true, widget: "auto" },
  { name: "title", label: "Title", readOnly: false, widget: "text" },
], view: {} };

function mockApi(objectDefinition: unknown = definition) {
  vi.mocked(api).mockImplementation(async (url, method = "GET") => {
    if (url === "/connections/1/tables") return ["things"] as never;
    if (url.includes("/schema/tables/things")) return schema as never;
    if (url.endsWith("/tables/things/object") && method === "GET") return objectDefinition as never;
    if (url.endsWith("/field-set-rules/validate") && method === "POST")
      return { message: "Field set rule formulas are valid." } as never;
    return undefined as never;
  });
}

afterEach(() => { cleanup(); vi.clearAllMocks(); });

describe("Object field workflow", () => {
  it("loads, edits, orders, deletes, and saves field set rules", async () => {
    mockApi({
      ...definition,
      fieldSetRules: [
        { field: "title", condition: "[title] = 'old'", value: "'first'" },
      ],
    });
    render(<ObjectEditor connections={[{ id: 1, name: "Local" } as never]} onChanged={() => {}} />);
    await screen.findByText("[title] = 'old'");

    fireEvent.click(screen.getByRole("button", { name: "Edit set rule 1" }));
    fireEvent.change(screen.getByLabelText("Set rule condition formula"), {
      target: { value: "Length([title]) > 3" },
    });
    fireEvent.change(screen.getByLabelText("Set rule value formula"), {
      target: { value: "Upper([title])" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Validate formulas" }));
    await screen.findByText("Field set rule formulas are valid.");
    expect(api).toHaveBeenCalledWith(
      "/admin/connections/1/tables/things/field-set-rules/validate",
      "POST",
      expect.objectContaining({
        field: "title",
        condition: "Length([title]) > 3",
        value: "Upper([title])",
      }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Save set rule" }));

    fireEvent.click(screen.getByRole("button", { name: "Add set rule" }));
    expect(Array.from((screen.getByLabelText("Set rule field") as HTMLSelectElement).options)
      .map((option) => option.value)).toContain("datatype");
    expect((screen.getByRole("button", { name: "Save set rule" }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByLabelText("Set rule condition formula"), {
      target: { value: "true" },
    });
    fireEvent.change(screen.getByLabelText("Set rule value formula"), {
      target: { value: "'fallback'" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save set rule" }));
    fireEvent.click(screen.getByRole("button", { name: "Move set rule 2 up" }));
    fireEvent.click(screen.getByRole("button", { name: "Delete set rule 2" }));
    fireEvent.click(screen.getByRole("button", { name: "Save object" }));

    await waitFor(() => expect(api).toHaveBeenCalledWith(
      "/admin/connections/1/tables/things/object",
      "PUT",
      expect.objectContaining({
        fieldSetRules: [
          { field: "title", condition: "true", value: "'fallback'" },
        ],
      }),
    ));
  });

  it("selects a datatype by label and stores its key as a rule formula", async () => {
    mockApi({
      ...definition,
      dataTypes: [
        { key: "default", label: "Standard customer", fields: [] },
        { key: "premium", label: "Premium customer", fields: [] },
      ],
      defaultDataTypeKey: "default",
    });
    render(<ObjectEditor connections={[{ id: 1, name: "Local" } as never]} onChanged={() => {}} />);
    await screen.findByText("varchar(100)");

    fireEvent.click(screen.getByRole("button", { name: "Add set rule" }));
    fireEvent.change(screen.getByLabelText("Set rule field"), { target: { value: "datatype" } });
    const select = screen.getByLabelText("Set rule data type") as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => [option.value, option.text])).toEqual([
      ["", "Select data type"],
      ["default", "Standard customer"],
      ["premium", "Premium customer"],
    ]);
    fireEvent.change(select, { target: { value: "premium" } });
    expect((screen.getByLabelText("Set rule value formula") as HTMLTextAreaElement).value).toBe("'premium'");
  });

  it("places object action buttons above the Data Types section", async () => {
    mockApi();
    render(<ObjectEditor connections={[{ id: 1, name: "Local" } as never]} onChanged={() => {}} />);
    await screen.findByText("varchar(100)");

    const addField = screen.getByRole("button", { name: "Add field" });
    const dataTypes = screen.getByRole("heading", { name: "Data Types" });
    expect(addField.compareDocumentPosition(dataTypes) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("keeps the list read-only and saves behavior from an accessible edit dialog", async () => {
    mockApi();
    render(<ObjectEditor connections={[{ id: 1, name: "Local" } as never]} onChanged={() => {}} />);
    await screen.findByText("varchar(100)");
    expect(screen.queryByLabelText("title control")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Edit field title" }));
    const dialog = screen.getByRole("dialog", { name: "Edit database field" });
    expect(dialog).toBeTruthy();
    expect(screen.getByLabelText("Control / behavior")).toBeTruthy();
    fireEvent.click(screen.getByLabelText("title readOnly"));
    fireEvent.click(screen.getByRole("button", { name: "Save field" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(api).not.toHaveBeenCalledWith(
      "/admin/connections/1/schema/tables/things/modify-column",
      "POST",
      expect.anything(),
    );
    expect(api).toHaveBeenCalledWith(
      "/admin/connections/1/tables/things/object",
      "PUT",
      expect.objectContaining({
        fields: expect.arrayContaining([
          expect.objectContaining({ name: "title", readOnly: true }),
        ]),
      }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Edit field title" }));
    fireEvent.change(screen.getByLabelText("Text length"), {
      target: { value: "120" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save field" }));
    await waitFor(() =>
      expect(api).toHaveBeenCalledWith(
        "/admin/connections/1/schema/tables/things/modify-column",
        "POST",
        expect.objectContaining({ name: "title", length: 120, type: "text" }),
      ),
    );
  });

  it("creates database fields from control choices without a type dropdown", async () => {
    mockApi();
    render(<ObjectEditor connections={[{ id: 1, name: "Local" } as never]} onChanged={() => {}} />);
    await screen.findByText("varchar(100)");
    fireEvent.click(screen.getByRole("button", { name: "Add field" }));
    expect(screen.getByRole("dialog", { name: "Add field" })).toBeTruthy();
    expect(screen.queryByLabelText("Column type")).toBeNull();
    fireEvent.change(screen.getByLabelText("Field name"), { target: { value: "enabled" } });
    fireEvent.change(screen.getByLabelText("Control / behavior"), { target: { value: "checkbox" } });
    fireEvent.click(screen.getByRole("button", { name: "Create field" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(api).toHaveBeenCalledWith(
      "/admin/connections/1/schema/tables/things/columns",
      "POST",
      expect.objectContaining({ name: "enabled", type: "boolean" }),
    );
    expect(api).toHaveBeenCalledWith(
      "/admin/connections/1/tables/things/object",
      "PUT",
      expect.objectContaining({
        fields: expect.arrayContaining([
          expect.objectContaining({ name: "enabled", widget: "checkbox" }),
        ]),
      }),
    );
  });

  it("moves required and mask controls into the default data type", async () => {
    mockApi({
      ...definition,
      dataTypes: undefined as never,
      defaultDataTypeKey: undefined as never,
      fields: definition.fields.map((field) =>
        field.name === "title"
          ? { ...field, widget: "auto", required: true, mask: { pattern: "AA-##?" } }
          : field,
      ),
    });
    render(<ObjectEditor connections={[{ id: 1, name: "Local" } as never]} onChanged={() => {}} />);
    await screen.findByText("varchar(100)");
    fireEvent.click(screen.getByRole("button", { name: "Edit field title" }));
    expect(screen.queryByLabelText("title required")).toBeNull();
    expect(screen.queryByLabelText("Regex-style mask pattern")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    fireEvent.click(screen.getByRole("button", { name: "Default" }));
    fireEvent.click(screen.getByRole("button", { name: "Configure title for Default" }));
    expect((screen.getByLabelText("Required") as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText("Regex-style mask pattern") as HTMLInputElement).value).toBe("AA-##?");
    expect(screen.queryByLabelText("Numbers only")).toBeNull();
    fireEvent.change(screen.getByLabelText("Regex-style mask pattern"), { target: { value: "###" } });
    fireEvent.click(screen.getByRole("button", { name: "Save field settings" }));
    await waitFor(() =>
      expect(api).toHaveBeenCalledWith(
        "/admin/connections/1/tables/things/object",
        "PUT",
        expect.objectContaining({
          fields: expect.arrayContaining([
            expect.not.objectContaining({ required: expect.anything(), mask: expect.anything() }),
          ]),
          defaultDataTypeKey: "default",
          dataTypes: expect.arrayContaining([
            expect.objectContaining({
              key: "default",
              fields: expect.arrayContaining([
                expect.objectContaining({ name: "title", required: true, mask: { pattern: "###" } }),
              ]),
            }),
          ]),
        }),
      ),
    );
  });

  it("refreshes the schema token after saving data types before resizing a field", async () => {
    let schemaReads = 0;
    vi.mocked(api).mockImplementation(async (url, method = "GET") => {
      if (url === "/connections/1/tables") return ["things"] as never;
      if (
        url === "/admin/connections/1/schema/tables/things" &&
        method === "GET"
      ) {
        schemaReads++;
        return {
          ...schema,
          version: schemaReads === 1 ? "v1" : "v2",
          columns: schemaReads === 1
            ? schema.columns
            : [
                ...schema.columns,
                {
                  name: "datatype",
                  type: "varchar",
                  sqlType: "varchar(64)",
                  nullable: false,
                  primaryKey: false,
                  autoIncrement: false,
                  generated: false,
                  length: 64,
                  precision: null,
                  scale: null,
                  default: "default",
                  relatedTable: null,
                  relatedKey: null,
                  uniqueKey: false,
                  editBlocked: "Managed by Data Types.",
                },
              ],
        } as never;
      }
      if (url.endsWith("/tables/things/object") && method === "GET")
        return definition as never;
      if (url.endsWith("/modify-column") && method === "POST")
        return { ...schema, version: "v3" } as never;
      return undefined as never;
    });

    render(<ObjectEditor connections={[{ id: 1, name: "Local" } as never]} onChanged={() => {}} />);
    await screen.findByText("varchar(100)");

    fireEvent.click(screen.getByRole("button", { name: "Default" }));
    fireEvent.click(screen.getByRole("button", { name: "Configure title for Default" }));
    fireEvent.click(screen.getByLabelText("Required"));
    fireEvent.click(screen.getByRole("button", { name: "Save field settings" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(schemaReads).toBe(2);

    fireEvent.click(screen.getByRole("button", { name: "Edit field title" }));
    fireEvent.change(screen.getByLabelText("Text length"), {
      target: { value: "120" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save field" }));

    await waitFor(() =>
      expect(api).toHaveBeenCalledWith(
        "/admin/connections/1/schema/tables/things/modify-column",
        "POST",
        expect.objectContaining({ name: "title", length: 120, version: "v2" }),
      ),
    );
  });

  it("adds a type, preserves its immutable key while editing, and makes it default", async () => {
    mockApi();
    render(<ObjectEditor connections={[{ id: 1, name: "Local" } as never]} onChanged={() => {}} />);
    await screen.findByText("varchar(100)");
    fireEvent.click(screen.getByRole("button", { name: "Add data type" }));
    fireEvent.change(screen.getByLabelText("Data type key"), { target: { value: "invoice" } });
    fireEvent.change(screen.getByLabelText("Data type label"), { target: { value: "Invoice" } });
    fireEvent.click(screen.getByRole("button", { name: "Save data type" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Add data type" })).toBeNull());
    expect(api).toHaveBeenCalledWith(
      "/admin/connections/1/tables/things/object",
      "PUT",
      expect.objectContaining({
        defaultDataTypeKey: "default",
        dataTypes: expect.arrayContaining([expect.objectContaining({ key: "invoice", label: "Invoice" })]),
      }),
    );
  });

  it("shows the managed datatype column and saves and reopens its selected default", async () => {
    mockApi({
      ...definition,
      dataTypes: [
        { key: "default", label: "Default", fields: definition.fields.map((field) => ({ name: field.name, required: false, mask: null, overrideDropdownOptions: false, enabledOptionKeys: [] })) },
        { key: "premium", label: "Premium", fields: definition.fields.map((field) => ({ name: field.name, required: false, mask: null, overrideDropdownOptions: false, enabledOptionKeys: [] })) },
      ],
      defaultDataTypeKey: "default",
    });
    render(<ObjectEditor connections={[{ id: 1, name: "Local" } as never]} onChanged={() => {}} />);

    await screen.findByText("varchar(100)");
    expect(screen.getByText("Managed")).toBeTruthy();
    expect(screen.getByText("Provisioned when object settings are saved")).toBeTruthy();
    const select = screen.getByLabelText("datatype default value") as HTMLSelectElement;
    expect(select.value).toBe("default");
    expect(Array.from(select.options).map((option) => [option.value, option.text])).toEqual([
      ["default", "Default"],
      ["premium", "Premium"],
    ]);

    fireEvent.change(select, { target: { value: "premium" } });
    await waitFor(() => expect(select.value).toBe("premium"));
    expect(api).toHaveBeenCalledWith(
      "/admin/connections/1/tables/things/object",
      "PUT",
      expect.objectContaining({ defaultDataTypeKey: "premium" }),
    );

    cleanup();
    mockApi({
      ...definition,
      dataTypes: [
        { key: "default", label: "Default", fields: definition.fields.map((field) => ({ name: field.name, required: false, mask: null, overrideDropdownOptions: false, enabledOptionKeys: [] })) },
        { key: "premium", label: "Premium", fields: definition.fields.map((field) => ({ name: field.name, required: false, mask: null, overrideDropdownOptions: false, enabledOptionKeys: [] })) },
      ],
      defaultDataTypeKey: "premium",
    });
    render(<ObjectEditor connections={[{ id: 1, name: "Local" } as never]} onChanged={() => {}} />);
    await screen.findByText("varchar(100)");
    expect((screen.getByLabelText("datatype default value") as HTMLSelectElement).value).toBe("premium");
  });

  it("creates new object fields as nullable and represents them in every data type", async () => {
    mockApi();
    render(<ObjectEditor connections={[{ id: 1, name: "Local" } as never]} onChanged={() => {}} />);
    await screen.findByText("varchar(100)");
    fireEvent.click(screen.getByRole("button", { name: "Add field" }));
    fireEvent.change(screen.getByLabelText("Field name"), { target: { value: "email" } });
    fireEvent.change(screen.getByLabelText("Control / behavior"), { target: { value: "email" } });
    expect(screen.queryByLabelText("email required")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Create field" }));
    await waitFor(() =>
      expect(api).toHaveBeenCalledWith(
        "/admin/connections/1/schema/tables/things/columns",
        "POST",
        expect.objectContaining({ name: "email", type: "text", length: 255, nullable: true }),
      ),
    );
    expect(api).toHaveBeenCalledWith(
      "/admin/connections/1/tables/things/object",
      "PUT",
      expect.objectContaining({
        dataTypes: expect.arrayContaining([
          expect.objectContaining({ fields: expect.arrayContaining([expect.objectContaining({ name: "email" })]) }),
        ]),
      }),
    );
  });

  it("does not derive database nullability from legacy object required metadata", async () => {
    vi.mocked(api).mockImplementation(async (url, method = "GET") => {
      if (url === "/connections/1/tables") return ["things"] as never;
      if (url.includes("/schema/tables/things"))
        return { ...schema, columns: schema.columns.map((column) => column.name === "title" ? { ...column, nullable: false } : column) } as never;
      if (url.endsWith("/tables/things/object") && method === "GET") return definition as never;
      return undefined as never;
    });
    render(<ObjectEditor connections={[{ id: 1, name: "Local" } as never]} onChanged={() => {}} />);
    await screen.findByText("varchar(100)");
    fireEvent.click(screen.getByRole("button", { name: "Edit field title" }));
    fireEvent.click(screen.getByRole("button", { name: "Save field" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(api).not.toHaveBeenCalledWith(
      "/admin/connections/1/schema/tables/things/modify-column",
      "POST",
      expect.anything(),
    );
  });

  it("requires explicit confirmation before deleting and calls delete after approval", async () => {
    mockApi();
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    render(<ObjectEditor connections={[{ id: 1, name: "Local" } as never]} onChanged={() => {}} />);
    await screen.findByText("varchar(100)");
    fireEvent.click(screen.getByRole("button", { name: "Delete field title" }));
    expect(confirm).toHaveBeenCalledOnce();
    expect(api).not.toHaveBeenCalledWith(expect.stringContaining("/fields/title"), "DELETE");
    confirm.mockReturnValue(true);
    fireEvent.click(screen.getByRole("button", { name: "Delete field title" }));
    await waitFor(() =>
      expect(api).toHaveBeenCalledWith(
        "/admin/connections/1/tables/things/fields/title",
        "DELETE",
      ),
    );
    confirm.mockRestore();
  });
});

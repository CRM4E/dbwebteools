// @vitest-environment jsdom
import React from "react";
import {
  render,
  screen,
  fireEvent,
  waitFor,
  cleanup,
} from "@testing-library/react";
import { afterEach, describe, it, expect, vi } from "vitest";
import { RecordEditor } from "./main";
afterEach(cleanup);
const columns = [
  {
    name: "id",
    type: "int",
    nullable: false,
    primaryKey: true,
    generated: false,
    autoIncrement: true,
    default: null,
  },
  {
    name: "name",
    type: "varchar",
    nullable: false,
    primaryKey: false,
    generated: false,
    autoIncrement: false,
    default: null,
  },
];
describe("Record editor", () => {
  it("omits generated primary keys and submits editable fields", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    render(
      <RecordEditor
        columns={columns}
        fields={[]}
        row={null}
        close={() => {}}
        save={save}
      />,
    );
    expect((screen.getByLabelText("id") as HTMLInputElement).disabled).toBe(
      true,
    );
    fireEvent.change(screen.getByLabelText("name"), {
      target: { value: "Alice" },
    });
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() => expect(save).toHaveBeenCalledWith({ name: "Alice" }));
  });
  it("keeps the form open and reports conflict errors", async () => {
    render(
      <RecordEditor
        columns={columns}
        fields={[]}
        row={{ values: { id: 1, name: "Alice" }, version: "old" }}
        close={() => {}}
        save={async () => {
          throw new Error("Record changed");
        }}
      />,
    );
    fireEvent.change(screen.getByLabelText("name"), {
      target: { value: "Changed" },
    });
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() =>
      expect(screen.getByRole("alert").textContent).toBe("Record changed"),
    );
  });
  it("clears nullable text and number controls to NULL without a Set NULL option", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    render(
      <RecordEditor
        columns={[
          { ...columns[1], nullable: true },
          {
            ...columns[1],
            name: "amount",
            type: "decimal",
            nullable: true,
          },
          { ...columns[1], name: "notes", type: "text", nullable: true },
        ]}
        fields={[
          {
            name: "amount",
            label: "Amount",
            section: "",
            order: 1,
            hidden: false,
            readOnly: false,
            widget: "number",
          },
          {
            name: "notes",
            label: "Notes",
            section: "",
            order: 2,
            hidden: false,
            readOnly: false,
            widget: "textarea",
          },
        ]}
        row={{
          values: { name: "Original", amount: "12.50", notes: "Original" },
          version: "old",
        }}
        close={() => {}}
        save={save}
      />,
    );
    expect(screen.queryByText("Set NULL")).toBeNull();
    fireEvent.change(screen.getByLabelText("name"), {
      target: { value: "" },
    });
    fireEvent.change(screen.getByLabelText("Amount"), {
      target: { value: "" },
    });
    fireEvent.change(screen.getByLabelText("Notes"), {
      target: { value: "" },
    });
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() =>
      expect(save).toHaveBeenCalledWith({
        name: null,
        amount: null,
        notes: null,
      }),
    );
  });
  it("hides database types and clears every nullable value control without a Set NULL toggle", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    render(
      <RecordEditor
        columns={[
          { ...columns[1], name: "stamp", type: "timestamp", nullable: true },
          { ...columns[1], name: "day", type: "date", nullable: true },
          { ...columns[1], name: "status", type: "varchar", nullable: true },
          { ...columns[1], name: "enabled", type: "tinyint", nullable: true },
          { ...columns[1], name: "confirmed", type: "tinyint", nullable: true },
        ]}
        fields={[
          {
            name: "status",
            label: "Status",
            section: "",
            order: 2,
            hidden: false,
            readOnly: false,
            widget: "dropdown",
            options: [{ key: "ready", display: "Ready" }],
          },
          {
            name: "enabled",
            label: "Enabled",
            section: "",
            order: 3,
            hidden: false,
            readOnly: false,
            widget: "checkbox",
          },
          {
            name: "confirmed",
            label: "Confirmed",
            section: "",
            order: 4,
            hidden: false,
            readOnly: false,
            required: true,
            widget: "checkbox",
          },
        ]}
        row={{
          values: {
            stamp: "2026-09-15T13:14:15",
            day: "2026-09-15",
            status: "ready",
            enabled: true,
            confirmed: null,
          },
          version: "old",
        }}
        close={() => {}}
        save={save}
      />,
    );
    expect(screen.queryByText("Set NULL", { exact: true })).toBeNull();
    for (const type of ["timestamp", "date", "varchar", "tinyint"])
      expect(screen.queryByText(type, { exact: true })).toBeNull();
    expect(screen.getByText("Enabled", { exact: true })).toBeTruthy();
    expect((screen.getByLabelText("Enabled") as HTMLSelectElement).value).toBe(
      "true",
    );
    const confirmed = screen.getByLabelText("Confirmed") as HTMLSelectElement;
    expect(confirmed.value).toBe("");
    expect(confirmed.required).toBe(true);

    fireEvent.change(screen.getByLabelText("stamp"), {
      target: { value: "" },
    });
    fireEvent.change(screen.getByLabelText("day"), {
      target: { value: "" },
    });
    fireEvent.change(screen.getByLabelText("Status"), {
      target: { value: "" },
    });
    fireEvent.change(screen.getByLabelText("Enabled"), {
      target: { value: "" },
    });
    fireEvent.change(confirmed, { target: { value: "false" } });
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() =>
      expect(save).toHaveBeenCalledWith({
        stamp: null,
        day: null,
        status: null,
        enabled: null,
        confirmed: false,
      }),
    );
  });
  it("requires non-nullable text on edit even when the database has a default", () => {
    render(
      <RecordEditor
        columns={[{ ...columns[1], default: "Database default" }]}
        fields={[]}
        row={{ values: { name: "Original" }, version: "old" }}
        close={() => {}}
        save={async () => {}}
      />,
    );
    expect((screen.getByLabelText("name") as HTMLInputElement).required).toBe(
      true,
    );
  });
  it("does not present a SQL NULL default as a value", () => {
    render(
      <RecordEditor
        columns={[{ ...columns[1], nullable: true, default: "NULL" }]}
        fields={[]}
        row={null}
        close={() => {}}
        save={async () => {}}
      />,
    );
    expect(
      (screen.getByLabelText("name") as HTMLInputElement).placeholder,
    ).toBe("");
    expect(screen.queryByPlaceholderText("Optional")).toBeNull();
    expect(screen.queryByPlaceholderText("Default: NULL")).toBeNull();
  });
  it("uses native email validation and submits empty optional email as NULL", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    render(
      <RecordEditor
        columns={[{ ...columns[1], name: "email", nullable: true }]}
        fields={[
          {
            name: "email",
            label: "Email address",
            section: "",
            order: 0,
            hidden: false,
            readOnly: false,
            widget: "email",
          },
        ]}
        row={{ values: { email: "person@example.com" }, version: "old" }}
        close={() => {}}
        save={save}
      />,
    );
    const input = screen.getByLabelText("Email address") as HTMLInputElement;
    expect(input.type).toBe("email");
    expect(input.autocomplete).toBe("email");
    expect(input.maxLength).toBe(255);
    fireEvent.change(input, { target: { value: "not-an-email" } });
    expect(input.checkValidity()).toBe(false);
    fireEvent.change(input, { target: { value: "" } });
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() => expect(save).toHaveBeenCalledWith({ email: null }));
  });
});

describe("Lookup editor validation", () => {
  it("requires selection for a required relation instead of submitting a blank key", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    render(
      <RecordEditor
        columns={[{ ...columns[1], name: "person_id", type: "bigint" }]}
        fields={[
          {
            name: "person_id",
            label: "Customer",
            section: "",
            order: 0,
            hidden: false,
            readOnly: false,
            widget: "lookup",
            lookup: {
              table: "people",
              keyColumn: "id",
              displayColumn: "name",
              searchColumns: [],
            },
          },
        ]}
        row={null}
        close={() => {}}
        save={save}
      />,
    );
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() =>
      expect(screen.getByRole("alert").textContent).toBe(
        "Select a related record for Customer.",
      ),
    );
    expect(save).not.toHaveBeenCalled();
  });
  it("leaves an optional relation omitted so a database default can apply", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    render(
      <RecordEditor
        columns={[
          { ...columns[1], name: "person_id", type: "bigint", nullable: true },
        ]}
        fields={[
          {
            name: "person_id",
            label: "Customer",
            section: "",
            order: 0,
            hidden: false,
            readOnly: false,
            widget: "lookup",
            lookup: {
              table: "people",
              keyColumn: "id",
              displayColumn: "name",
              searchColumns: [],
            },
          },
        ]}
        row={null}
        close={() => {}}
        save={save}
      />,
    );
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() => expect(save).toHaveBeenCalledWith({}));
  });
  it("lets legacy masked values remain unchanged during unrelated edits", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    render(
      <RecordEditor
        columns={[
          { ...columns[1], nullable: true },
          { ...columns[1], name: "notes", nullable: true },
        ]}
        fields={[
          {
            name: "name",
            label: "Reference code",
            section: "",
            order: 0,
            hidden: false,
            readOnly: false,
            widget: "text",
            mask: {
              characterSet: "alphanumeric",
              minimumLength: 6,
              requiredCharacters: "-",
            },
          },
        ]}
        row={{ values: { name: "OLD", notes: "Before" }, version: "old" }}
        close={() => {}}
        save={save}
      />,
    );
    const masked = screen.getByLabelText("Reference code") as HTMLInputElement;
    expect(masked.minLength).toBe(-1);
    fireEvent.change(screen.getByLabelText("notes"), {
      target: { value: "After" },
    });
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() => expect(save).toHaveBeenCalledWith({ notes: "After" }));
    fireEvent.change(masked, { target: { value: "NEW" } });
    expect(masked.minLength).toBe(6);
  });

  it("lets existing exact-mask violations remain unchanged during unrelated edits", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    render(
      <RecordEditor
        columns={[
          { ...columns[1], nullable: true },
          { ...columns[1], name: "notes", nullable: true },
        ]}
        fields={[
          {
            name: "name",
            label: "Reference code",
            section: "",
            order: 0,
            hidden: false,
            readOnly: false,
            widget: "text",
            mask: { pattern: "###-###" },
          },
        ]}
        row={{ values: { name: "OLD", notes: "Before" }, version: "old" }}
        close={() => {}}
        save={save}
      />,
    );
    const masked = screen.getByLabelText("Reference code") as HTMLInputElement;
    expect(masked.minLength).toBe(-1);
    expect(masked.maxLength).toBe(-1);
    fireEvent.change(screen.getByLabelText("notes"), {
      target: { value: "After" },
    });
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() => expect(save).toHaveBeenCalledWith({ notes: "After" }));
    fireEvent.change(masked, { target: { value: "123-456" } });
    expect(masked.minLength).toBe(7);
    expect(masked.maxLength).toBe(7);
  });

  it("shows the field-mask tip and validates changed values before saving", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    render(
      <RecordEditor
        columns={[{ ...columns[1], nullable: true }]}
        fields={[
          {
            name: "name",
            label: "Reference code",
            section: "",
            order: 0,
            hidden: false,
            readOnly: false,
            widget: "text",
            mask: { pattern: "###-###?" },
          },
        ]}
        row={null}
        close={() => {}}
        save={save}
      />,
    );
    expect(screen.getByText(/Required format: Format: ###-###\?/)).toBeTruthy();
    const input = screen.getByLabelText("Reference code") as HTMLInputElement;
    expect(input.minLength).toBe(6);
    expect(input.maxLength).toBe(7);
    expect(input.getAttribute("aria-describedby")).toBe("mask-tip-name");
    fireEvent.change(input, { target: { value: "123_45" } });
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() =>
      expect(screen.getByRole("alert").textContent).toContain(
        "Reference code must match the input mask.",
      ),
    );
    expect(save).not.toHaveBeenCalled();
    fireEvent.change(input, { target: { value: "123-45" } });
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() => expect(save).toHaveBeenCalledWith({ name: "123-45" }));

    fireEvent.change(input, { target: { value: "123-456" } });
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() => expect(save).toHaveBeenLastCalledWith({ name: "123-456" }));
  });
});

describe("Date, timestamp and dropdown controls", () => {
  const temporalColumns = [
    columns[1],
    { ...columns[1], name: "stamp", type: "timestamp" },
    { ...columns[1], name: "day", type: "date" },
  ];
  const row = {
    values: {
      name: "Original",
      stamp: "2026-09-15T13:14:15.123456",
      day: "2026-09-15",
    },
    version: "version",
  };
  it("prefills native date/time inputs and preserves microseconds when another field changes", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    render(
      <RecordEditor
        columns={temporalColumns}
        fields={[]}
        row={row}
        close={() => {}}
        save={save}
      />,
    );
    expect((screen.getByLabelText("stamp") as HTMLInputElement).type).toBe(
      "datetime-local",
    );
    expect((screen.getByLabelText("stamp") as HTMLInputElement).value).toBe(
      "2026-09-15T13:14:15.123",
    );
    expect((screen.getByLabelText("day") as HTMLInputElement).value).toBe(
      "2026-09-15",
    );
    fireEvent.change(screen.getByLabelText("name"), {
      target: { value: "Updated" },
    });
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() => expect(save).toHaveBeenCalledWith({ name: "Updated" }));
  });
  it("supports a date-only editor on a timestamp without resubmitting the hidden time", async () => {
    const save = vi.fn().mockResolvedValue(undefined),
      close = vi.fn();
    render(
      <RecordEditor
        columns={temporalColumns}
        fields={[
          {
            name: "stamp",
            label: "Stamp",
            section: "",
            order: 0,
            hidden: false,
            readOnly: false,
            widget: "date",
          },
        ]}
        row={row}
        close={close}
        save={save}
      />,
    );
    expect((screen.getByLabelText("Stamp") as HTMLInputElement).value).toBe(
      "2026-09-15",
    );
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() => expect(close).toHaveBeenCalled());
    expect(save).not.toHaveBeenCalled();
    fireEvent.change(screen.getByLabelText("Stamp"), {
      target: { value: "2026-10-20" },
    });
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() =>
      expect(save).toHaveBeenCalledWith({ stamp: "2026-10-20" }),
    );
  });
  it("shows dropdown labels but submits only the selected key", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    render(
      <RecordEditor
        columns={[columns[1]]}
        fields={[
          {
            name: "name",
            label: "Status",
            section: "",
            order: 0,
            hidden: false,
            readOnly: false,
            widget: "dropdown",
            options: [
              { key: "draft", display: "Draft document" },
              { key: "ready", display: "Ready to publish" },
            ],
            enabledOptionKeys: ["ready"],
          },
        ]}
        row={null}
        close={() => {}}
        save={save}
      />,
    );
    expect(
      screen.getByRole("option", { name: "Ready to publish" }),
    ).toBeTruthy();
    expect(screen.queryByRole("option", { name: "Draft document" })).toBeNull();
    fireEvent.change(screen.getByLabelText("Status"), {
      target: { value: "ready" },
    });
    fireEvent.click(screen.getByText("Save record"));
    await waitFor(() => expect(save).toHaveBeenCalledWith({ name: "ready" }));
  });

  it("shows a disabled existing dropdown label but rejects it as a new choice", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    const field = {
      name: "name", label: "Status", section: "", order: 0, hidden: false,
      readOnly: false, widget: "dropdown", enabledOptionKeys: ["ready"],
      options: [
        { key: "draft", display: "Draft document" },
        { key: "ready", display: "Ready to publish" },
      ],
    };
    render(
      <RecordEditor
        columns={[columns[1]]}
        fields={[field]}
        row={{ values: { name: "draft" }, version: "v1" }}
        close={() => {}}
        save={save}
      />,
    );
    const legacy = screen.getByRole("option", { name: "Draft document (not available)" }) as HTMLOptionElement;
    expect(legacy.disabled).toBe(true);
    cleanup();

    render(
      <RecordEditor
        columns={[columns[1]]}
        fields={[field]}
        row={null}
        initialValues={{ name: "draft" }}
        close={() => {}}
        save={save}
      />,
    );
    fireEvent.click(screen.getByText("Save record"));
    expect((await screen.findByRole("alert")).textContent).toContain(
      "Status is not an enabled dropdown value.",
    );
    expect(save).not.toHaveBeenCalled();
  });
});

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { createClient } from "@supabase/supabase-js";

const SupabaseUrl = z.string().url();

function getRequiredEnv(name: string): string {
  const value = process.env[name];
  if (!value) {
    throw new Error(`Missing required env var: ${name}`);
  }
  return value;
}

const supabaseUrl = SupabaseUrl.parse(getRequiredEnv("SUPABASE_URL"));
const serviceRoleKey = getRequiredEnv("SUPABASE_SERVICE_ROLE_KEY");

const allowlist = (process.env.SUPABASE_TABLE_ALLOWLIST ?? "")
  .split(",")
  .map((t) => t.trim())
  .filter(Boolean);

function assertTableAllowed(table: string) {
  if (allowlist.length === 0) return;
  if (!allowlist.includes(table)) {
    throw new Error(
      `Table '${table}' is not in SUPABASE_TABLE_ALLOWLIST (${allowlist.join(", ")})`
    );
  }
}

const supabase = createClient(supabaseUrl, serviceRoleKey, {
  auth: { persistSession: false, autoRefreshToken: false },
});

const server = new McpServer({ name: "supabase-local-rw", version: "0.1.0" });

const JsonRecord = z.record(z.unknown());

function toJsonText(value: unknown): string {
  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

function jsonResult(value: unknown) {
  return { content: [{ type: "text" as const, text: toJsonText(value) }] };
}

server.registerTool(
  "sb_select",
  {
    description: "Select rows from a Supabase table.",
    inputSchema: z.object({
      table: z.string().min(1),
      columns: z.string().default("*"),
      eq: z
        .record(z.union([z.string(), z.number(), z.boolean(), z.null()]))
        .optional(),
      limit: z.number().int().min(1).max(1000).default(100),
      orderBy: z.string().optional(),
      ascending: z.boolean().default(true),
    }),
    annotations: {
      readOnlyHint: true,
      idempotentHint: true,
    },
  },
  async ({ table, columns, eq, limit, orderBy, ascending }, _extra) => {
    assertTableAllowed(table);

    let query = supabase.from(table).select(columns).limit(limit);

    if (eq) {
      for (const [key, value] of Object.entries(eq)) {
        query = query.eq(key, value as any);
      }
    }

    if (orderBy) {
      query = query.order(orderBy, { ascending });
    }

    const { data, error } = await query;
    if (error) throw new Error(error.message);

    return jsonResult(data ?? []);
  }
);

server.registerTool(
  "sb_insert",
  {
    description: "Insert row(s) into a Supabase table.",
    inputSchema: z.object({
      table: z.string().min(1),
      rows: z.array(JsonRecord).min(1),
      returning: z.string().default("*"),
    }),
    annotations: {
      destructiveHint: true,
    },
  },
  async ({ table, rows, returning }, _extra) => {
    assertTableAllowed(table);

    const { data, error } = await supabase.from(table).insert(rows).select(returning);
    if (error) throw new Error(error.message);

    return jsonResult(data ?? []);
  }
);

server.registerTool(
  "sb_update",
  {
    description: "Update row(s) in a Supabase table.",
    inputSchema: z.object({
      table: z.string().min(1),
      values: JsonRecord,
      match: z.record(z.union([z.string(), z.number(), z.boolean(), z.null()])),
      returning: z.string().default("*"),
    }),
    annotations: {
      destructiveHint: true,
    },
  },
  async ({ table, values, match, returning }, _extra) => {
    assertTableAllowed(table);

    let query = supabase.from(table).update(values);
    for (const [key, value] of Object.entries(match)) {
      query = query.eq(key, value as any);
    }

    const { data, error } = await query.select(returning);
    if (error) throw new Error(error.message);

    return jsonResult(data ?? []);
  }
);

server.registerTool(
  "sb_delete",
  {
    description: "Delete row(s) from a Supabase table.",
    inputSchema: z.object({
      table: z.string().min(1),
      match: z.record(z.union([z.string(), z.number(), z.boolean(), z.null()])),
      returning: z.string().default("*"),
    }),
    annotations: {
      destructiveHint: true,
    },
  },
  async ({ table, match, returning }, _extra) => {
    assertTableAllowed(table);

    let query = supabase.from(table).delete();
    for (const [key, value] of Object.entries(match)) {
      query = query.eq(key, value as any);
    }

    const { data, error } = await query.select(returning);
    if (error) throw new Error(error.message);

    return jsonResult(data ?? []);
  }
);

server.registerTool(
  "sb_rpc",
  {
    description: "Call a Supabase RPC (Postgres function).",
    inputSchema: z.object({
      fn: z.string().min(1),
      args: z.record(z.unknown()).default({}),
    }),
    annotations: {
      idempotentHint: false,
    },
  },
  async ({ fn, args }, _extra) => {
    const { data, error } = await supabase.rpc(fn, args);
    if (error) throw new Error(error.message);

    return jsonResult(data ?? null);
  }
);

await server.connect(new StdioServerTransport());

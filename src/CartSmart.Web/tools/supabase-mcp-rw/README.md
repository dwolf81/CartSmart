# Supabase MCP (local read/write)

This MCP server exposes a small set of generic Supabase tools (select/insert/update/delete/rpc) for **local development**.

## Security

This setup uses `SUPABASE_SERVICE_ROLE_KEY`, which has broad read/write access and bypasses RLS.
Only run it on a trusted machine and never commit keys.

## Run (manual)

From `src/CartSmart.Web`:

```powershell
cd .\tools\supabase-mcp-rw
npm install
$env:SUPABASE_URL="https://YOUR_PROJECT.supabase.co"
$env:SUPABASE_SERVICE_ROLE_KEY="YOUR_SERVICE_ROLE_KEY"
npx tsx .\src\index.ts
```

## VS Code MCP

This repo is already configured in `.vscode/mcp.json` with a server named `supabase_local_rw`.
When VS Code starts the server it will prompt you for the URL and key.

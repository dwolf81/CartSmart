-- Purge old ingest_log entries while preserving AI decision records
-- (ai_rejected / ai_approved) which are needed to avoid re-validating
-- the same listings on subsequent ingest runs.

create or replace function f_purge_old_ingest_logs(p_retention_days integer)
returns integer
language plpgsql
as $$
declare
  v_deleted integer;
begin
  delete from ingest_log
  where created_at < now() - make_interval(days => p_retention_days)
    and (ignore_reason is null
         or (ignore_reason not ilike 'ai_rejected%'
             and ignore_reason not ilike 'ai_approved%'));

  get diagnostics v_deleted = row_count;
  return v_deleted;
end;
$$;

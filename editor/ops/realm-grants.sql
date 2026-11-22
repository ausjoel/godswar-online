-- Run as the database owner (godswar) once, for Operations -> Servers (add / remove a realm).
-- UPDATE on public.server is already granted (the realm name edit uses it).
GRANT INSERT, DELETE ON public.server TO gweditor;
-- Adding a realm fires trg_server_time_zone_insert_audit (AFTER INSERT, not SECURITY DEFINER), which inserts into
-- server_time_zone_audit as gweditor; the "never reuse an id" check reads it. Its id is an IDENTITY column (no
-- sequence grant needed). Check first with:  \dp public.server_time_zone_audit
GRANT SELECT, INSERT ON public.server_time_zone_audit TO gweditor;
-- The Servers tab counts rows per table that references server(id); a table gweditor cannot read shows "?" and blocks
-- removal of that realm. List them with:  SELECT conrelid::regclass FROM pg_constraint WHERE confrelid = 'public.server'::regclass;

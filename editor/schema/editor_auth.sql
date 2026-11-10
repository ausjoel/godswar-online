-- Apply with the database owner's migration role, before enabling --account-auth.
BEGIN;
ALTER TABLE public.accounts ADD COLUMN IF NOT EXISTS editor_admin boolean NOT NULL DEFAULT false;
COMMENT ON COLUMN public.accounts.editor_admin IS 'Editor authorization only. Independent of character GM powers.';
COMMIT;

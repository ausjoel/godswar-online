-- Read-only snapshot of the reborn schema for build_glossary.py. Run:
--   ssh vps 'sudo -n -u postgres psql -X -q -v ON_ERROR_STOP=1 godswar' < snapshot.sql > live-schema-meta.json
BEGIN READ ONLY;
\pset tuples_only on
\pset format unaligned
SELECT jsonb_pretty(jsonb_build_object(
 'columns', (SELECT jsonb_agg(jsonb_build_object('s',table_schema,'t',table_name,'c',column_name,'n',ordinal_position,'type',CASE WHEN data_type IN ('USER-DEFINED','ARRAY') THEN udt_name ELSE data_type END,'len',character_maximum_length,'prec',numeric_precision,'scale',numeric_scale,'null',is_nullable,'def',column_default,'gen',is_generated,'genexpr',generation_expression,'ident',is_identity) ORDER BY table_schema,table_name,ordinal_position) FROM information_schema.columns c WHERE table_schema IN ('public','legacy') AND EXISTS (SELECT 1 FROM information_schema.tables t WHERE t.table_schema=c.table_schema AND t.table_name=c.table_name AND t.table_type='BASE TABLE')),
 'constraints', (SELECT jsonb_agg(jsonb_build_object('s',n.nspname,'t',r.relname,'name',con.conname,'type',con.contype,'def',pg_get_constraintdef(con.oid),'cols',(SELECT jsonb_agg(a.attname) FROM unnest(con.conkey) k JOIN pg_attribute a ON a.attrelid=con.conrelid AND a.attnum=k),'ref',CASE WHEN con.contype='f' THEN (SELECT rn.nspname||'.'||rr.relname FROM pg_class rr JOIN pg_namespace rn ON rn.oid=rr.relnamespace WHERE rr.oid=con.confrelid) END)) FROM pg_constraint con JOIN pg_class r ON r.oid=con.conrelid JOIN pg_namespace n ON n.oid=r.relnamespace WHERE n.nspname IN ('public','legacy')),
 'indexes', (SELECT jsonb_agg(jsonb_build_object('s',schemaname,'t',tablename,'name',indexname,'def',indexdef)) FROM pg_indexes WHERE schemaname IN ('public','legacy')),
 'triggers', (SELECT jsonb_agg(jsonb_build_object('s',n.nspname,'t',r.relname,'name',tg.tgname,'def',pg_get_triggerdef(tg.oid))) FROM pg_trigger tg JOIN pg_class r ON r.oid=tg.tgrelid JOIN pg_namespace n ON n.oid=r.relnamespace WHERE NOT tg.tgisinternal AND n.nspname IN ('public','legacy')),
 'comments', (SELECT jsonb_agg(jsonb_build_object('t',c.relname,'col',a.attname,'d',d.description)) FROM pg_description d JOIN pg_class c ON c.oid=d.objoid LEFT JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum=d.objsubid AND d.objsubid>0 JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN ('public','legacy')),
 'rows', (SELECT jsonb_object_agg(n.nspname||'.'||c.relname, (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', n.nspname, c.relname), false, true, '')))[1]::text::bigint) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.relkind='r' AND n.nspname IN ('public','legacy')),
 'measured', jsonb_build_object('database', current_database()||' (read-only snapshot)', 'date', to_char(now() AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI"Z"')),
 'views', (SELECT jsonb_agg(table_name) FROM information_schema.views WHERE table_schema IN ('public','legacy'))
));
ROLLBACK;

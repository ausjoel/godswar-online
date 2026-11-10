# Contributing

- Keep personal details out of tracked files. Your server address, keys, notes and test
  accounts belong in `config/godswar.env` and `local/` (git-ignored).
- Code reads addresses from configuration: environment variables, `config/godswar.env`, or
  build flags. Do not write an IP or domain into source.
- Before you commit: `python3 -I tools/leak-scan.py`. Put your own patterns (your IP, your
  domain, your name) in `local/leak-patterns.txt` so they are checked too.
- Server changes: see `server/README.md` and run the protocol checks
  (`dotnet run --project server/tests/Godswar.Server.ProtocolChecks -c Release`).
- Keep `LICENSE` and `NOTICE.md` intact; this project is GPL-3.0 and builds on earlier open-source work.

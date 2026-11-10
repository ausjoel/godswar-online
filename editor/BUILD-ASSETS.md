# Building the editor's pictures from your own client

Item icons, window frames and mini-maps come from the game client, which is not distributed here.

```bash
cd editor
python3 -I build_ui_kit.py --help
python3 -I build_origin_catalog.py --help
```
Point both at your client folder. They write into `static/ui`, `static/*.png` and `catalog-origin/`.
Those outputs are git-ignored; do not commit them.

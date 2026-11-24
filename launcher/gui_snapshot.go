package main

import (
	"fmt"
	"image/png"
	"os"
	"path/filepath"
)

// sampleNews is only for --ui-snapshot pictures.
var sampleNews = []NewsItem{
	{ID: "a", Date: "23 Nov 2026", Title: "NPC windows no longer miss your OK", URL: "https://" + NewsHost + "/news",
		Summary: "If the online-players line or another message-board line arrived while you had an NPC window open, your next OK could do nothing and you had to click again. Board lines now wait until you have finished with NPCs."},
	{ID: "b", Date: "23 Nov 2026", Title: "The Class Shifter works: change your weapon or armour to your new class", URL: "https://" + NewsHost + "/news"},
	{ID: "c", Date: "22 Nov 2026", Title: "Dragon pieces in the Mid-autumn Gift Pack", URL: "https://" + NewsHost + "/news"},
}

// writeSnapshots renders the window's main states to PNG files (no window, no screen capture needed).
func writeSnapshots(dir string) error {
	uiScale = scaleFromEnv(1) // GW_UI_SCALE=2: Retina-size pictures
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	tmp, err := os.MkdirTemp("", "gwui")
	if err != nil {
		return err
	}
	defer os.RemoveAll(tmp)
	writeState(tmp, State{ClientVersion: "1.5.7", ClientRoot: tmp, Engine: EngineWine})
	shots := []struct {
		name     string
		platform string
		set      func(a *guiApp)
	}{
		{"01-checking", "windows", func(a *guiApp) {}},
		{"02-downloading", "windows", func(a *guiApp) {
			a.busy, a.server, a.news = true, "online", sampleNews
			a.progress, a.status, a.detail = 0.42, "Downloading game 1.5.8  42%", "245 of 584 MB"
		}},
		{"03-ready", "windows", func(a *guiApp) {
			a.root, a.server, a.news, a.status = tmp, "online", sampleNews, "Ready to play. Press START."
		}},
		{"04-ready-hover-start", "mac", func(a *guiApp) {
			a.root, a.server, a.news, a.status, a.sel = tmp, "online", sampleNews, "Ready to play. Press START.", 1
			a.render()
			a.hover = "start"
		}},
		{"05-settings-mac", "mac", func(a *guiApp) {
			a.root, a.server, a.news, a.status, a.settings = tmp, "online", sampleNews, "Ready to play. Press START.", true
		}},
		{"06-settings-windows", "windows", func(a *guiApp) {
			a.root, a.server, a.news, a.status, a.settings = tmp, "online", sampleNews, "Ready to play. Press START.", true
		}},
		{"07-offline", "windows", func(a *guiApp) {
			a.root, a.server, a.news = tmp, "offline", sampleNews
			a.status, a.detail = "Could not check for updates. You can still play.", "Get https://YOUR-DOMAIN.example/downloads/manifest.json: no such host"
		}},
	}
	for _, s := range shots {
		a := newGUIApp(&Config{DataDir: tmp, Platform: s.platform, Version: Version, Engine: EngineWine})
		s.set(a)
		f, err := os.Create(filepath.Join(dir, s.name+".png"))
		if err != nil {
			return err
		}
		err = png.Encode(f, a.render())
		f.Close()
		if err != nil {
			return err
		}
		fmt.Println("wrote", f.Name())
	}
	return nil
}

package main

import (
	"image"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestSetIniValueKeepsFile(t *testing.T) {
	p := filepath.Join(t.TempDir(), "config.ini")
	orig := "[SERVER]\r\nPORT=6999\r\n\r\n[SYSTEM]\r\nLocals=en_us\r\nAvatar=0\r\n\r\n[DEBUG]\r\nAVATAR=1\r\n"
	os.WriteFile(p, []byte(orig), 0o644)
	if err := setIniValue(p, "SYSTEM", "Avatar", "2"); err != nil {
		t.Fatal(err)
	}
	b, _ := os.ReadFile(p)
	want := strings.Replace(orig, "Avatar=0", "Avatar=2", 1)
	if string(b) != want {
		t.Fatalf("got %q", b)
	}
	if iniValue(p, "DEBUG", "AVATAR") != "1" || iniValue(p, "SYSTEM", "avatar") != "2" {
		t.Fatal("other section changed or lookup failed")
	}
	// missing key: added at the end of its section, before the blank line
	os.WriteFile(p, []byte("[SYSTEM]\r\nLocals=en_us\r\n\r\n[DEBUG]\r\nX=1\r\n"), 0o644)
	setIniValue(p, "SYSTEM", "Avatar", "1")
	b, _ = os.ReadFile(p)
	if string(b) != "[SYSTEM]\r\nLocals=en_us\r\nAvatar=1\r\n\r\n[DEBUG]\r\nX=1\r\n" {
		t.Fatalf("add: got %q", b)
	}
}

func TestApplyModels(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)
	root := t.TempDir()
	// Windows: config.ini
	os.WriteFile(filepath.Join(root, "config.ini"), []byte("[SYSTEM]\nAvatar=0\n"), 0o644)
	if _, err := applyModels(root, "windows", EngineWine, ModelsNew); err != nil {
		t.Fatal(err)
	}
	if iniValue(filepath.Join(root, "config.ini"), "SYSTEM", "Avatar") != "2" {
		t.Fatal("windows config.ini not set")
	}
	if installedModels(State{ClientRoot: root}, "windows", EngineWine) != ModelsNew {
		t.Fatal("windows read-back")
	}
	// Mac Wine without a settings file: a one-line settings.conf (launcher.sh ignores GW_AVATAR from the environment)
	os.MkdirAll(filepath.Join(root, "GodsWar.app", "Contents"), 0o755)
	os.WriteFile(filepath.Join(root, "GodsWar.app", "Contents", "Info.plist"), []byte("<key>GWVariant</key>\n\t<string>GodsWar</string>"), 0o644)
	sc := filepath.Join(home, "Library", "Application Support", "GodsWar", "settings.conf")
	if env, err := applyModels(root, "mac", EngineWine, ModelsNew); err != nil || env != nil {
		t.Fatalf("mac new file: %v %v", env, err)
	}
	if keyValue(sc, "GW_AVATAR") != `"2"` {
		t.Fatal("mac new settings.conf")
	}
	// Mac Wine with a settings file: the GW_AVATAR line is replaced, the rest kept
	os.Remove(sc)
	os.WriteFile(sc, []byte("# GodsWar client settings\nGW_WIDTH=\"1366\"\nGW_AVATAR=\"1\"\nGW_FKEYS=\"fn\"\n"), 0o600)
	if env, err := applyModels(root, "mac", EngineWine, ModelsNew); err != nil || env != nil {
		t.Fatalf("mac file: %v %v", env, err)
	}
	b, _ := os.ReadFile(sc)
	if string(b) != "# GodsWar client settings\nGW_WIDTH=\"1366\"\nGW_AVATAR=\"2\"\nGW_FKEYS=\"fn\"\n" {
		t.Fatalf("settings.conf: %q", b)
	}
	if st, _ := os.Stat(sc); st.Mode().Perm() != 0o600 {
		t.Fatal("mode changed")
	}
	if installedModels(State{ClientRoot: root}, "mac", EngineWine) != ModelsNew {
		t.Fatal("mac read-back")
	}
	// Native: settings.ini inside the app
	ni := filepath.Join(root, NativeApp, "Contents", "Resources", "settings.ini")
	os.MkdirAll(filepath.Dir(ni), 0o755)
	os.WriteFile(ni, []byte("# avatar: 2 = new\navatar=2\nsound=1\n"), 0o644)
	applyModels(root, "mac", EngineNative, ModelsOriginal)
	b, _ = os.ReadFile(ni)
	if string(b) != "# avatar: 2 = new\navatar=1\nsound=1\n" {
		t.Fatalf("native: %q", b)
	}
	if installedModels(State{NativeRoot: root}, "mac", EngineNative) != ModelsOriginal {
		t.Fatal("native read-back")
	}
	// Nothing set yet: the client's own defaults (original on Windows/Wine, new on Native)
	if installedModels(State{}, "windows", EngineWine) != ModelsOriginal || installedModels(State{}, "mac", EngineNative) != ModelsNew {
		t.Fatal("defaults")
	}
}

type fakeWin struct{ frames, quits int }

func (f *fakeWin) Present(*image.RGBA) { f.frames++ }
func (f *fakeWin) Quit()               { f.quits++ }

func centre(r image.Rectangle) (int, int) { return (r.Min.X + r.Max.X) / 2, (r.Min.Y + r.Max.Y) / 2 }

func press(a *guiApp, r image.Rectangle) bool {
	x, y := centre(r)
	a.render()
	on := a.onEvent(evDown, x, y)
	a.onEvent(evUp, x, y)
	return on
}

func TestGUISettingsAndButtons(t *testing.T) {
	dir := t.TempDir()
	t.Setenv("HOME", dir)
	writeState(dir, State{})
	clicks := make(chan string, 10)
	oldOpen := openURLFn
	openURLFn = func(u string) { clicks <- u }
	defer func() { openURLFn = oldOpen }()
	a := newGUIApp(&Config{DataDir: dir, Platform: "mac", Version: Version, Engine: EngineWine})
	w := &fakeWin{}
	a.win = w
	a.news = sampleNews
	// START is greyed until the client is ready: pressing it is a window drag, not a click.
	if press(a, rStart) {
		t.Fatal("START live without a client")
	}
	// Website opens the player site.
	if !press(a, leftButton(0)) {
		t.Fatal("Website not a control")
	}
	if u := <-clicks; u != "https://"+NewsHost+"/" {
		t.Fatal(u)
	}
	// Settings: pick New models and Native, Cancel keeps the old values.
	a.click("settings")
	a.click("models2")
	a.click("engineNative")
	a.click("cancel")
	if readState(dir).Models != "" || a.cfg.Engine != EngineWine {
		t.Fatal("cancel saved something")
	}
	// Save stores the models; the engine is unchanged so no update starts.
	a.click("settings")
	a.click("models2")
	a.click("confirm")
	if readState(dir).Models != ModelsNew || a.settings {
		t.Fatalf("save: %+v", readState(dir))
	}
	// While Settings is open only its controls answer.
	a.click("settings")
	a.render()
	if h := a.hit(centre(rStart)); h != "" {
		t.Fatalf("background control %q live under Settings", h)
	}
	a.click("cancel")
	// A news title selects it; "Read the full post" opens only the player site.
	a.click("news1")
	if a.sel != 1 {
		t.Fatal("news select")
	}
	a.news[1].URL = "https://evil.example/x"
	a.click("read")
	select {
	case u := <-clicks:
		t.Fatalf("opened %s", u)
	default:
	}
	// Close quits the window.
	a.click("close")
	if w.quits != 1 {
		t.Fatal("close")
	}
}

func TestSnapshotsRender(t *testing.T) {
	d := t.TempDir()
	if err := writeSnapshots(d); err != nil {
		t.Fatal(err)
	}
	ents, _ := os.ReadDir(d)
	if len(ents) < 7 {
		t.Fatalf("%d snapshots", len(ents))
	}
}

// Retina (review d147): the frame is drawn at 2x with the same layout, and clicks still use window points.
func TestRender2x(t *testing.T) {
	uiScale = 2
	defer func() { uiScale = 1 }()
	a := newGUIApp(&Config{DataDir: t.TempDir(), Platform: "mac", Version: Version, Engine: EngineWine})
	a.news, a.settings = sampleNews, true
	img := a.render()
	if img.Rect.Dx() != 2*guiW || img.Rect.Dy() != 2*guiH {
		t.Fatalf("2x frame %v", img.Rect)
	}
	if h := a.hit(centre(rPanel)); h != "" {
		t.Logf("panel centre hit %q", h)
	}
	a.settings = false
	a.render()
	if a.hit(centre(leftButton(3))) != "settings" {
		t.Fatal("Settings button not under its window-point rectangle at 2x")
	}
}

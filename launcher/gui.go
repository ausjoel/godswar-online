package main

// The launcher window (launcher 1.0.5+): the original GodsWar launcher art with news, server status, a progress bar,
// Update, Settings (character models; on a Mac also Wine or Native) and START. Headless runs (--no-launch, --engine,
// --show-news, --no-gui, or no window available) keep the 1.0.4 dialogs, so old launchers and tests behave as before.

import (
	"errors"
	"fmt"
	"image"
	"image/color"
	"image/png"
	"net"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"time"
)

const guiW, guiH = 785, 541

// Event kinds from the window shells.
const (
	evMove  = 1
	evDown  = 2
	evUp    = 3
	evLeave = 4
	evClose = 5
)

// window is what a platform shell gives the app.
type window interface {
	Present(img *image.RGBA)
	Quit()
}

// Avatar values of the client's config.ini [SYSTEM] Avatar (Launcher/README: 1 = Characters, 2 = Characters_new).
const (
	ModelsOriginal = "1"
	ModelsNew      = "2"
)

type guiApp struct {
	cfg *Config
	win window

	mu        sync.Mutex
	status    string
	detail    string
	progress  float64 // 0..1, <0 = hidden
	busy      bool
	root      string // installed client root once ready
	failed    bool
	news      []NewsItem
	sel       int // selected news item
	server    string
	hover     string
	press     string
	settings  bool
	setModels string
	setEngine string
	started   bool
	buttons   []guiButton
	dirty     chan struct{}
}

type guiButton struct {
	id      string
	r       image.Rectangle
	enabled bool
}

// Layout (coordinates in the 785x541 art).
var (
	rEvents   = image.Rect(84, 184, 348, 262)
	rServers  = image.Rect(84, 324, 348, 434)
	rNews     = image.Rect(392, 184, 722, 412)
	rProgress = image.Rect(84, 470, 452, 488)
	rUpdate   = image.Rect(462, 452, 576, 488)
	rStart    = image.Rect(592, 440, 741, 495)
	rClose    = image.Rect(664, 94, 738, 126)
	rPanel    = image.Rect(212, 160, 574, 420)
)

func leftButton(i int) image.Rectangle { return image.Rect(10, 162+31*i, 72, 189+31*i) }

func newGUIApp(cfg *Config) *guiApp {
	st := readState(cfg.DataDir)
	a := &guiApp{cfg: cfg, progress: -1, server: "checking", status: "Checking for updates...", dirty: make(chan struct{}, 1)}
	a.setModels = a.currentModels(st)
	a.setEngine = cfg.Engine
	return a
}

// currentModels: the saved choice, else what the installed client's config.ini says. The client uses the new
// models only for Avatar=2 (Client-RE LEDGER E1); 0, 1 or no value means the original ones.
func (a *guiApp) currentModels(st State) string {
	if st.Models == ModelsOriginal || st.Models == ModelsNew {
		return st.Models
	}
	return installedModels(st, a.cfg.Platform, a.cfg.Engine)
}

func (a *guiApp) set(f func()) {
	a.mu.Lock()
	f()
	a.mu.Unlock()
	a.redraw()
}

// redraw asks the draw loop for a new frame; callers (downloads, clicks) never wait for drawing.
func (a *guiApp) redraw() {
	if a.win == nil {
		return
	}
	select {
	case a.dirty <- struct{}{}:
	default: // a frame is already due
	}
}

// drawLoop renders and shows frames, at most about 30 a second.
func (a *guiApp) drawLoop() {
	for range a.dirty {
		a.present()
		time.Sleep(33 * time.Millisecond)
	}
}

func (a *guiApp) present() {
	img := a.render()
	a.win.Present(img)
	if d := os.Getenv("GW_UI_DUMP"); d != "" { // testing: the frame now on screen, as a PNG
		tmp := filepath.Join(d, "launcher-window.tmp")
		if f, err := os.Create(tmp); err == nil {
			png.Encode(f, img)
			f.Close()
			os.Rename(tmp, filepath.Join(d, "launcher-window.png"))
		}
	}
}

// render draws the whole window.
func (a *guiApp) render() *image.RGBA {
	a.mu.Lock()
	defer a.mu.Unlock()
	img := image.NewRGBA(image.Rect(0, 0, guiW*uiScale, guiH*uiScale))
	if bg := artImg("bg"); bg != nil {
		blit(img, bg, image.Point{})
	} else {
		plainBackground(img)
	}
	a.buttons = a.buttons[:0]
	add := func(id string, r image.Rectangle, en bool) bool {
		a.buttons = append(a.buttons, guiButton{id, r, en})
		return a.hover == id && en
	}
	// Left column (drawn over the original Website/Register/Points/Forum buttons).
	for i, b := range [][2]string{{"site", "Website"}, {"register", "Register"}, {"guides", "Guides"}, {"settings", "Settings"}} {
		r := leftButton(i)
		hot := add(b[0], r, !a.settings)
		pill(img, r, b[1], 12, hot, true)
	}
	// Close
	switch hot := add("close", rClose, true); {
	case artImg("close") == nil:
		pill(img, rClose.Inset(2), "Close", 13, hot, true)
	case hot:
		blit(img, artImg("close_hi"), rClose.Min)
	default:
		blit(img, artImg("close"), rClose.Min)
	}
	a.drawEvents(img, add)
	a.drawServers(img)
	a.drawNews(img, add)
	// Status, progress, Update and START.
	fs := face(13, true)
	drawText(img, fs, rProgress.Min.X, rProgress.Min.Y-7, ellipsize(fs, a.status, rProgress.Dx()), solid(color.RGBA{0x14, 0x3c, 0x6c, 255}))
	if a.progress >= 0 {
		roundRect(img, rProgress, 8, func(int) color.RGBA { return color.RGBA{0xf4, 0xfb, 0xff, 255} })
		w := int(float64(rProgress.Dx()-4) * a.progress)
		if w > 0 {
			fr := image.Rect(rProgress.Min.X+2, rProgress.Min.Y+2, rProgress.Min.X+2+w, rProgress.Max.Y-2)
			g := vgrad{fr.Min.Y, fr.Max.Y, color.RGBA{0xff, 0xd8, 0x50, 255}, color.RGBA{0xe8, 0x60, 0x10, 255}}
			roundRect(img, fr, 6, func(y int) color.RGBA { return g.At(0, y).(color.RGBA) })
		}
		ring(img, rProgress, 8, 1.5, color.RGBA{0x2a, 0x6c, 0xb0, 255})
	}
	if a.detail != "" {
		fd := face(11, false)
		drawText(img, fd, rProgress.Min.X, rProgress.Max.Y+14, ellipsize(fd, a.detail, rProgress.Dx()+120), solid(color.RGBA{0x2a, 0x50, 0x78, 255}))
	}
	upLabel := "Update"
	if a.busy {
		upLabel = "Working..."
	}
	pill(img, rUpdate, upLabel, 14, add("update", rUpdate, !a.busy && !a.settings && !a.started), !a.busy && !a.started)
	canStart := a.root != "" && !a.busy && !a.started
	switch {
	case artImg("start") == nil:
		pill(img, rStart.Inset(3), "START", 24, add("start", rStart, canStart && !a.settings), canStart)
	case !canStart:
		add("start", rStart, false)
		blit(img, artImg("start_off"), rStart.Min)
	case add("start", rStart, !a.settings):
		blit(img, artImg("start_hi"), rStart.Min)
	default:
		blit(img, artImg("start"), rStart.Min)
	}
	if a.settings {
		a.drawSettings(img, add)
	}
	return img
}

func (a *guiApp) drawEvents(img *image.RGBA, add func(string, image.Rectangle, bool) bool) {
	f := face(12, false)
	fb := face(12, true)
	if len(a.news) == 0 {
		drawText(img, f, rEvents.Min.X+4, rEvents.Min.Y+18, "News appears here after the update check.", solid(color.RGBA{0x40, 0x60, 0x80, 255}))
		return
	}
	for i, n := range a.news {
		r := image.Rect(rEvents.Min.X, rEvents.Min.Y+2+i*25, rEvents.Max.X, rEvents.Min.Y+26+i*25)
		hot := add(fmt.Sprintf("news%d", i), r, !a.settings)
		if i == a.sel || hot {
			c := color.RGBA{0xff, 0xff, 0xff, 150}
			if i == a.sel {
				c = color.RGBA{0xff, 0xf2, 0xc0, 230}
			}
			roundRect(img, r.Inset(1), 6, func(int) color.RGBA { return c })
		}
		d := shortDate(n.Date)
		drawText(img, fb, r.Min.X+6, r.Min.Y+17, d, solid(color.RGBA{0xc0, 0x50, 0x00, 255}))
		drawText(img, f, r.Min.X+56, r.Min.Y+17, ellipsize(f, n.Title, r.Dx()-62), solid(color.RGBA{0x10, 0x30, 0x58, 255}))
	}
}

// shortDate turns "23 Nov 2026" into "23 Nov".
func shortDate(s string) string {
	p := strings.Fields(s)
	if len(p) >= 2 {
		return p[0] + " " + p[1]
	}
	return s
}

func (a *guiApp) drawServers(img *image.RGBA) {
	fb := face(14, true)
	f := face(12, false)
	x, y := rServers.Min.X+6, rServers.Min.Y+18
	dot := color.RGBA{0xb0, 0xb0, 0xb0, 255}
	state := "Checking..."
	switch a.server {
	case "online":
		dot, state = color.RGBA{0x2c, 0xc8, 0x40, 255}, "Online"
	case "offline":
		dot, state = color.RGBA{0xe0, 0x30, 0x20, 255}, "Offline"
	}
	roundRect(img, image.Rect(x, y-11, x+12, y+1), 6, func(int) color.RGBA { return dot })
	ring(img, image.Rect(x, y-11, x+12, y+1), 6, 1, color.RGBA{0x30, 0x30, 0x30, 200})
	drawText(img, fb, x+20, y, "GodsWar Online", solid(color.RGBA{0x10, 0x30, 0x58, 255}))
	drawText(img, fb, rServers.Max.X-8-textWidth(fb, state), y, state, solid(color.RGBA{0x10, 0x30, 0x58, 255}))
	st := readState(a.cfg.DataDir)
	lines := []string{}
	if a.cfg.Platform == "mac" {
		eng := "Wine"
		if a.cfg.Engine == EngineNative {
			eng = "Native (Beta)"
		}
		lines = append(lines, "Runs with: "+eng)
	}
	ver := st.ClientVersion
	if a.cfg.Engine == EngineNative {
		ver = st.NativeVersion
	}
	if ver != "" {
		lines = append(lines, "Game "+ver+"   Launcher "+a.cfg.Version)
	} else {
		lines = append(lines, "Launcher "+a.cfg.Version)
	}
	for i, l := range lines {
		drawText(img, f, x, y+26+i*20, l, solid(color.RGBA{0x24, 0x48, 0x70, 255}))
	}
}

func (a *guiApp) drawNews(img *image.RGBA, add func(string, image.Rectangle, bool) bool) {
	x, w := rNews.Min.X+12, rNews.Dx()-24
	y := rNews.Min.Y + 26
	fh := face(17, true)
	if len(a.news) == 0 {
		drawFancy(img, fh, x, y, "Welcome to GodsWar", color.RGBA{0xff, 0xe0, 0x60, 255}, color.RGBA{0xf0, 0x70, 0x10, 255}, color.RGBA{0x6a, 0x22, 0x00, 255}, 1)
		f := face(13, false)
		for i, l := range wrapText(f, "The launcher keeps the game up to date. Press START when it is ready.", w) {
			drawText(img, f, x, y+30+i*18, l, solid(color.RGBA{0x20, 0x30, 0x40, 255}))
		}
		return
	}
	n := a.news[a.sel]
	for i, l := range wrapText(fh, n.Title, w) {
		if i == 2 {
			break
		}
		drawFancy(img, fh, x, y+i*22, l, color.RGBA{0xff, 0xe0, 0x60, 255}, color.RGBA{0xf0, 0x70, 0x10, 255}, color.RGBA{0x6a, 0x22, 0x00, 255}, 1)
		y += 0
	}
	tl := len(wrapText(fh, n.Title, w))
	if tl > 2 {
		tl = 2
	}
	y += (tl-1)*22 + 20
	fd := face(11, true)
	drawText(img, fd, x, y, n.Date, solid(color.RGBA{0x80, 0x88, 0x90, 255}))
	y += 22
	f := face(13, false)
	maxLines := (rNews.Max.Y - 40 - y) / 18
	ls := wrapText(f, n.Summary, w)
	if len(ls) > maxLines {
		ls = ls[:maxLines]
		ls[maxLines-1] = ellipsize(f, ls[maxLines-1]+" ...", w)
	}
	for i, l := range ls {
		drawText(img, f, x, y+i*18, l, solid(color.RGBA{0x20, 0x30, 0x40, 255}))
	}
	fl := face(13, true)
	label := "Read the full post"
	lr := image.Rect(x, rNews.Max.Y-30, x+textWidth(fl, label)+8, rNews.Max.Y-10)
	hot := add("read", lr, !a.settings)
	c := color.RGBA{0x1a, 0x5c, 0xc0, 255}
	if hot {
		c = color.RGBA{0xe0, 0x60, 0x00, 255}
	}
	drawText(img, fl, x, rNews.Max.Y-15, label, solid(c))
	fillRect(img, image.Rect(x, rNews.Max.Y-13, x+textWidth(fl, label), rNews.Max.Y-12), c)
}

func (a *guiApp) drawSettings(img *image.RGBA, add func(string, image.Rectangle, bool) bool) {
	dim(img, 0.6)
	a.buttons = a.buttons[:0] // only the panel is live
	p := rPanel
	g := vgrad{p.Min.Y, p.Max.Y, color.RGBA{0xf0, 0xfa, 0xff, 255}, color.RGBA{0xb4, 0xe0, 0xfa, 255}}
	roundRect(img, p, 14, func(y int) color.RGBA { return g.At(0, y).(color.RGBA) })
	hd := image.Rect(p.Min.X+4, p.Min.Y+4, p.Max.X-4, p.Min.Y+38)
	hg := vgrad{hd.Min.Y, hd.Max.Y, color.RGBA{0x7c, 0xc4, 0xf4, 255}, color.RGBA{0x2c, 0x80, 0xd0, 255}}
	roundRect(img, hd, 11, func(y int) color.RGBA { return hg.At(0, y).(color.RGBA) })
	ring(img, p, 14, 2, color.RGBA{0x1d, 0x4f, 0x8f, 255})
	drawFancy(img, face(18, true), p.Min.X+18, p.Min.Y+28, "Game Settings", color.RGBA{0xff, 0xf0, 0x80, 255}, color.RGBA{0xff, 0x90, 0x10, 255}, color.RGBA{0x6a, 0x22, 0x00, 255}, 1)
	fb := face(13, true)
	f := face(13, false)
	y := p.Min.Y + 66
	radio := func(id, label string, on bool, x, y int) {
		r := image.Rect(x, y-15, x+24+textWidth(f, label), y+6)
		hot := add(id, r, true)
		if on && artImg("check_on") != nil {
			blit(img, artImg("check_on"), image.Pt(x, y-15))
		} else if on {
			b := image.Rect(x+1, y-14, x+19, y+4)
			roundRect(img, b, 3, func(int) color.RGBA { return color.RGBA{0x3a, 0x8c, 0xe0, 255} })
			roundRect(img, b.Inset(5), 2, func(int) color.RGBA { return color.RGBA{0xff, 0xff, 0xff, 255} })
		} else {
			b := image.Rect(x+1, y-14, x+19, y+4)
			roundRect(img, b, 3, func(int) color.RGBA { return color.RGBA{0xff, 0xff, 0xff, 255} })
			ring(img, b, 3, 1.5, color.RGBA{0x3a, 0x78, 0xb8, 255})
		}
		c := color.RGBA{0x10, 0x30, 0x58, 255}
		if hot {
			c = color.RGBA{0xc0, 0x50, 0x00, 255}
		}
		drawText(img, f, x+24, y, label, solid(c))
	}
	drawText(img, fb, p.Min.X+18, y, "Character models", solid(color.RGBA{0x10, 0x30, 0x58, 255}))
	radio("models1", "V1", a.setModels == ModelsOriginal, p.Min.X+30, y+26) // review d144: "V1 / V2"
	radio("models2", "V2", a.setModels == ModelsNew, p.Min.X+170, y+26)
	y += 40
	drawText(img, face(11, false), p.Min.X+30, y+12, "Takes effect the next time the game starts.", solid(color.RGBA{0x50, 0x68, 0x80, 255}))
	if a.cfg.Platform == "mac" {
		y += 44
		drawText(img, fb, p.Min.X+18, y, "Run the game with", solid(color.RGBA{0x10, 0x30, 0x58, 255}))
		radio("engineWine", "Wine (recommended)", a.setEngine != EngineNative, p.Min.X+30, y+26)
		radio("engineNative", "Native (Beta)", a.setEngine == EngineNative, p.Min.X+200, y+26)
	}
	cr := image.Rect(p.Max.X-176, p.Max.Y-46, p.Max.X-100, p.Max.Y-12)
	xr := image.Rect(p.Max.X-90, p.Max.Y-46, p.Max.X-14, p.Max.Y-12)
	pill(img, cr, "Save", 14, add("confirm", cr, true), true)
	pill(img, xr, "Cancel", 14, add("cancel", xr, true), true)
}

// hit returns the enabled button under (x,y).
func (a *guiApp) hit(x, y int) string {
	a.mu.Lock()
	defer a.mu.Unlock()
	for i := len(a.buttons) - 1; i >= 0; i-- {
		b := a.buttons[i]
		if b.enabled && (image.Point{x, y}).In(b.r) {
			return b.id
		}
	}
	return ""
}

// onEvent handles a mouse event from the shell; it returns true when the press is on a control (no window drag).
func (a *guiApp) onEvent(kind, x, y int) bool {
	if kind != evMove && os.Getenv("GW_UI_TRACE") != "" && a.cfg.Logf != nil { // testing
		a.cfg.Logf("ui event %d at %d,%d -> %q", kind, x, y, a.hit(x, y))
	}
	switch kind {
	case evMove, evLeave:
		h := ""
		if kind == evMove {
			h = a.hit(x, y)
		}
		a.mu.Lock()
		changed := h != a.hover
		a.hover = h
		a.mu.Unlock()
		if changed {
			a.redraw()
		}
	case evDown:
		h := a.hit(x, y)
		a.mu.Lock()
		a.press = h
		a.mu.Unlock()
		return h != ""
	case evUp:
		h := a.hit(x, y)
		a.mu.Lock()
		p := a.press
		a.press = ""
		a.mu.Unlock()
		if h != "" && h == p {
			go a.click(h)
		}
	case evClose:
		a.quit()
	}
	return false
}

func (a *guiApp) quit() {
	if a.win != nil {
		a.win.Quit()
	}
}

func (a *guiApp) click(id string) {
	switch {
	case id == "close":
		a.quit()
	case id == "site":
		openURLFn("https://" + NewsHost + "/")
	case id == "register":
		openURLFn("https://" + NewsHost + "/signup")
	case id == "guides":
		openURLFn("https://" + NewsHost + "/guides")
	case id == "read":
		a.mu.Lock()
		u := ""
		if a.sel < len(a.news) {
			u = a.news[a.sel].URL
		}
		a.mu.Unlock()
		if u != "" && safeNewsURL(u) {
			openURLFn(u)
		}
	case strings.HasPrefix(id, "news"):
		var i int
		fmt.Sscanf(id, "news%d", &i)
		a.set(func() { a.sel = i })
	case id == "settings":
		a.set(func() {
			a.settings = true
			a.setModels = a.currentModels(readState(a.cfg.DataDir))
			a.setEngine = a.cfg.Engine
		})
	case id == "models1":
		a.set(func() { a.setModels = ModelsOriginal })
	case id == "models2":
		a.set(func() { a.setModels = ModelsNew })
	case id == "engineWine":
		a.set(func() { a.setEngine = EngineWine })
	case id == "engineNative":
		a.set(func() { a.setEngine = EngineNative })
	case id == "cancel":
		a.set(func() { a.settings = false })
	case id == "confirm":
		a.confirmSettings()
	case id == "update":
		a.runUpdate()
	case id == "start":
		a.start()
	}
}

func (a *guiApp) confirmSettings() {
	a.mu.Lock()
	models, engine := a.setModels, a.setEngine
	a.settings = false
	a.mu.Unlock()
	st := readState(a.cfg.DataDir)
	st.Models = models
	changed := a.cfg.Platform == "mac" && engine != a.cfg.Engine
	if changed {
		st.Engine = engine
		a.cfg.Engine = engine
	}
	writeState(a.cfg.DataDir, st)
	a.redraw()
	if changed {
		a.set(func() { a.root = "" })
		a.runUpdate()
	}
}

// runUpdate checks the manifest and brings the launcher and the game up to date, showing progress.
func (a *guiApp) runUpdate() {
	a.mu.Lock()
	if a.busy {
		a.mu.Unlock()
		return
	}
	a.busy = true
	a.failed = false
	a.status, a.detail, a.progress = "Checking for updates...", "", -1
	a.mu.Unlock()
	a.redraw()
	go a.checkServer()
	before := readState(a.cfg.DataDir)
	root, err := Update(a.cfg)
	after := readState(a.cfg.DataDir)
	a.set(func() {
		a.busy = false
		a.progress = -1
		a.detail = ""
		a.news = CleanNews(a.cfg.News)
		if a.sel >= len(a.news) {
			a.sel = 0
		}
		switch {
		case err == nil:
			a.root = root
			a.status = "Ready to play. Press START."
		case after.Root(a.cfg.Engine) != "":
			a.root = after.Root(a.cfg.Engine)
			a.status = "Could not check for updates. You can still play."
			a.detail = err.Error()
		default:
			a.failed = true
			a.status = "Could not download GodsWar. Press Update to try again."
			a.detail = err.Error()
		}
	})
	if a.cfg.Logf != nil {
		a.cfg.Logf("update: root=%q err=%v", root, err)
	}
	// A newer launcher was downloaded: restart into it so the player always runs the newest one.
	if after.LauncherPath != "" && after.LauncherVersion != before.LauncherVersion && CompareVersions(after.LauncherVersion, a.cfg.Version) > 0 {
		a.set(func() { a.status = "The launcher was updated. Restarting..." })
		time.Sleep(800 * time.Millisecond)
		restartLauncher(a.cfg)
	}
}

func (a *guiApp) checkServer() {
	s := "offline"
	c, err := net.DialTimeout("tcp", NewsHost+":6999", 4*time.Second)
	if err == nil {
		c.Close()
		s = "online"
	}
	a.set(func() { a.server = s })
}

func (a *guiApp) start() {
	a.mu.Lock()
	root := a.root
	models := readState(a.cfg.DataDir).Models // only a choice made in Settings is written; "" leaves the game as it is
	if root == "" || a.busy || a.started {
		a.mu.Unlock()
		return
	}
	a.started, a.busy = true, true
	a.status = "Starting GodsWar..."
	a.mu.Unlock()
	a.redraw()
	env, err := applyModels(root, a.cfg.Platform, a.cfg.Engine, models)
	if err != nil && a.cfg.Logf != nil {
		a.cfg.Logf("could not set the character models: %v", err)
	}
	ensureRuntime(a.cfg, false)
	if err := LaunchEnv(root, a.cfg.Platform, a.cfg.Engine, env); err != nil {
		a.set(func() {
			a.started, a.busy = false, false
			a.status = "Could not start GodsWar."
			a.detail = err.Error()
		})
		return
	}
	time.Sleep(1500 * time.Millisecond)
	a.quit()
}

// runGUI shows the launcher window; it returns an error when no window can be shown (the caller falls back).
func runGUI(cfg *Config) error {
	a := newGUIApp(cfg)
	if os.Getenv("GW_UI_STACKS") != "" { // testing: every goroutine's stack, every 5 s, into the data folder
		go func() {
			for range time.Tick(5 * time.Second) {
				buf := make([]byte, 1<<20)
				os.WriteFile(filepath.Join(cfg.DataDir, "stacks.txt"), buf[:runtime.Stack(buf, true)], 0o644)
			}
		}()
	}
	cfg.Status = func(s string) { a.set(func() { a.status = s }) }
	cfg.Progress = func(what string, done, total int64) {
		a.set(func() {
			if total > 0 {
				a.progress = float64(done) / float64(total)
				a.status = fmt.Sprintf("%s  %d%%", what, int(a.progress*100))
				a.detail = fmt.Sprintf("%d of %d MB", done>>20, total>>20)
			} else {
				a.progress = -1
				a.status = what
				a.detail = ""
			}
		})
	}
	cfg.OnFirstInstall = func(mb int64) {
		a.set(func() {
			a.status = fmt.Sprintf("Downloading GodsWar (about %d MB). This can take several minutes.", mb)
		})
	}
	if msg := rosettaProblem(); msg != "" && cfg.Engine == EngineWine {
		a.status, a.detail = "GodsWar needs Rosetta on this Mac.", msg
	}
	return runWindow(guiW, guiH, "GodsWar", a.onEvent, func(w window) {
		a.win = w
		go a.drawLoop()
		a.redraw()
		if a.detail == "" {
			go a.runUpdate()
		}
	})
}

var errNoWindow = errors.New("no launcher window on this system")

// guiLog appends to DataDir/launcher.log (the Windows GUI build has no console).
func guiLog(dir string) func(string, ...any) {
	return func(f string, args ...any) {
		line := time.Now().Format("2006-01-02 15:04:05 ") + fmt.Sprintf(f, args...) + "\n"
		fmt.Print(line[20:])
		if fh, err := os.OpenFile(filepath.Join(dir, "launcher.log"), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o644); err == nil {
			fh.WriteString(line)
			fh.Close()
		}
	}
}

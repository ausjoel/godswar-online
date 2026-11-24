package main

// Drawing helpers for the launcher window (launcher 1.0.5+). Everything is drawn in Go into an *image.RGBA, so the
// window looks the same on Mac and Windows and --ui-snapshot can write it to a PNG without a screen capture.

import (
	"bytes"
	"embed"
	"image"
	"image/color"
	"image/draw"
	"image/png"
	"math"
	"os"
	"strings"
	"sync"
	"unsafe"

	xdraw "golang.org/x/image/draw"
	"golang.org/x/image/font"
	"golang.org/x/image/font/gofont/gobold"
	"golang.org/x/image/font/gofont/goregular"
	"golang.org/x/image/font/opentype"
	"golang.org/x/image/math/fixed"
)

// The original GodsWar launcher art (IGG, from the retail 2.51 Launch.exe), magenta made transparent. The folder
// may be absent (public export): the window then draws a plain background (see assets/README.md).
//
//go:embed assets
var iggAssets embed.FS

var (
	artOnce sync.Once
	art     map[string]*image.RGBA
)

func artImg(name string) *image.RGBA {
	artOnce.Do(func() {
		art = map[string]*image.RGBA{}
		ents, _ := iggAssets.ReadDir("assets/igg")
		for _, e := range ents {
			if !strings.HasSuffix(e.Name(), ".png") {
				continue
			}
			b, _ := iggAssets.ReadFile("assets/igg/" + e.Name())
			im, err := png.Decode(bytes.NewReader(b))
			if err != nil {
				continue
			}
			rgba := image.NewRGBA(im.Bounds())
			draw.Draw(rgba, rgba.Bounds(), im, im.Bounds().Min, draw.Src)
			art[strings.TrimSuffix(e.Name(), ".png")] = rgba
		}
	})
	return art[name]
}

// uiScale is the number of device pixels per window point: 2 on a Retina Mac (review d147), else 1. All drawing
// helpers take window points (the 785x541 layout) and draw at this scale; set it once, before the first frame.
var uiScale = 1

// scaleFromEnv: GW_UI_SCALE=1 or 2 overrides the screen's scale (testing, --ui-snapshot).
func scaleFromEnv(def int) int {
	switch os.Getenv("GW_UI_SCALE") {
	case "1":
		return 1
	case "2":
		return 2
	}
	return def
}

func scaleRect(r image.Rectangle) image.Rectangle {
	return image.Rect(r.Min.X*uiScale, r.Min.Y*uiScale, r.Max.X*uiScale, r.Max.Y*uiScale)
}

var (
	fontOnce        sync.Once
	fontReg, fontBd *opentype.Font
	faces           = map[[2]int]font.Face{}
	facesMu         sync.Mutex
)

// face returns a cached Go font face (BSD licence, part of golang.org/x/image).
func face(size int, bold bool) font.Face {
	fontOnce.Do(func() {
		fontReg, _ = opentype.Parse(goregular.TTF)
		fontBd, _ = opentype.Parse(gobold.TTF)
	})
	k := [2]int{size * uiScale, 0}
	f := fontReg
	if bold {
		k[1], f = 1, fontBd
	}
	facesMu.Lock()
	defer facesMu.Unlock()
	if fc, ok := faces[k]; ok {
		return fc
	}
	fc, _ := opentype.NewFace(f, &opentype.FaceOptions{Size: float64(size * uiScale), DPI: 72, Hinting: font.HintingFull})
	faces[k] = fc
	return fc
}

// textWidth, ascent and descent are in window points.
func textWidth(f font.Face, s string) int {
	return (font.MeasureString(f, s).Ceil() + uiScale - 1) / uiScale
}
func ascent(f font.Face) int  { return (f.Metrics().Ascent.Ceil() + uiScale - 1) / uiScale }
func descent(f font.Face) int { return (f.Metrics().Descent.Ceil() + uiScale - 1) / uiScale }

// drawText draws s with its baseline at y.
func drawText(dst *image.RGBA, f font.Face, x, y int, s string, src image.Image) {
	d := font.Drawer{Dst: dst, Src: src, Face: f, Dot: fixed.P(x*uiScale, y*uiScale)}
	d.DrawString(s)
}

func solid(c color.RGBA) image.Image { return image.NewUniform(c) }

// vgrad is a vertical gradient between y0 (top colour) and y1 (bottom colour), in window points; At takes device
// pixels (it is used as a drawing source and as roundRect's fill), so it stays smooth at 2x.
type vgrad struct {
	y0, y1   int
	top, bot color.RGBA
}

func (g vgrad) ColorModel() color.Model { return color.RGBAModel }
func (g vgrad) Bounds() image.Rectangle {
	return image.Rect(-1e9, -1e9, 1e9, 1e9)
}
func (g vgrad) At(x, y int) color.Color {
	t := 0.0
	if g.y1 > g.y0 {
		yy := (float64(y) + 0.5) / float64(uiScale)
		t = math.Max(0, math.Min(1, (yy-float64(g.y0))/float64(g.y1-g.y0)))
	}
	l := func(a, b uint8) uint8 { return uint8(float64(a) + (float64(b)-float64(a))*t + 0.5) }
	return color.RGBA{l(g.top.R, g.bot.R), l(g.top.G, g.bot.G), l(g.top.B, g.bot.B), l(g.top.A, g.bot.A)}
}

// drawFancy is the original launcher's heading style: gold gradient letters with a dark outline.
func drawFancy(dst *image.RGBA, f font.Face, x, y int, s string, top, bot, outline color.RGBA, ow int) {
	for dy := -ow; dy <= ow; dy++ {
		for dx := -ow; dx <= ow; dx++ {
			if dx*dx+dy*dy <= ow*ow+1 && (dx != 0 || dy != 0) {
				drawText(dst, f, x+dx, y+dy, s, solid(outline))
			}
		}
	}
	drawText(dst, f, x, y, s, vgrad{y - ascent(f), y, top, bot})
}

// wrapText splits s into lines no wider than w.
func wrapText(f font.Face, s string, w int) []string {
	var lines []string
	for _, para := range strings.Split(s, "\n") {
		line := ""
		for _, word := range strings.Fields(para) {
			try := word
			if line != "" {
				try = line + " " + word
			}
			if textWidth(f, try) <= w || line == "" {
				line = try
				continue
			}
			lines = append(lines, line)
			line = word
		}
		lines = append(lines, line)
	}
	return lines
}

// ellipsize cuts s to fit w pixels.
func ellipsize(f font.Face, s string, w int) string {
	if textWidth(f, s) <= w {
		return s
	}
	r := []rune(s)
	for len(r) > 0 && textWidth(f, string(r)+"...") > w {
		r = r[:len(r)-1]
	}
	return strings.TrimRight(string(r), " ,;:.") + "..."
}

// blend draws c over dst at (x,y) with coverage a (0..1), premultiplied.
func blend(dst *image.RGBA, x, y int, c color.RGBA, a float64) {
	if !(image.Point{x, y}.In(dst.Rect)) || a <= 0 {
		return
	}
	if a > 1 {
		a = 1
	}
	i := dst.PixOffset(x, y)
	p := dst.Pix[i : i+4 : i+4]
	sa := float64(c.A) / 255 * a
	for k, v := range [3]uint8{c.R, c.G, c.B} {
		p[k] = uint8(float64(v)*sa + float64(p[k])*(1-sa) + 0.5)
	}
	p[3] = uint8(255*sa + float64(p[3])*(1-sa) + 0.5)
}

// roundRect fills r (window points) with radius rad, colour from fill(device y), anti-aliased corners.
func roundRect(dst *image.RGBA, r image.Rectangle, rad float64, fill func(y int) color.RGBA) {
	r, rad = scaleRect(r), rad*float64(uiScale)
	for y := r.Min.Y; y < r.Max.Y; y++ {
		c := fill(y)
		for x := r.Min.X; x < r.Max.X; x++ {
			blend(dst, x, y, c, cover(r, rad, x, y))
		}
	}
}

// cover is the pixel coverage of (x,y) inside the rounded rectangle.
func cover(r image.Rectangle, rad float64, x, y int) float64 {
	px, py := float64(x)+0.5, float64(y)+0.5
	cx := math.Max(float64(r.Min.X)+rad, math.Min(px, float64(r.Max.X)-rad))
	cy := math.Max(float64(r.Min.Y)+rad, math.Min(py, float64(r.Max.Y)-rad))
	d := math.Hypot(px-cx, py-cy)
	return math.Max(0, math.Min(1, rad-d+0.5))
}

// fillRect fills r (window points) with c.
func fillRect(dst *image.RGBA, r image.Rectangle, c color.RGBA) {
	draw.Draw(dst, scaleRect(r), image.NewUniform(c), image.Point{}, draw.Over)
}

// ring draws a rounded outline of width w (window points).
func ring(dst *image.RGBA, r image.Rectangle, rad, w float64, c color.RGBA) {
	r, rad, w = scaleRect(r), rad*float64(uiScale), w*float64(uiScale)
	in := r.Inset(int(w))
	for y := r.Min.Y; y < r.Max.Y; y++ {
		for x := r.Min.X; x < r.Max.X; x++ {
			a := cover(r, rad, x, y) - cover(in, math.Max(0, rad-w), x, y)
			blend(dst, x, y, c, a)
		}
	}
}

// pill is a launcher button in the style of the original art: blue glass with an orange outlined label.
func pill(dst *image.RGBA, r image.Rectangle, label string, size int, hot, enabled bool) {
	rad := float64(r.Dy()) / 2.4
	top, bot := color.RGBA{0xc8, 0xec, 0xff, 255}, color.RGBA{0x3c, 0x9c, 0xe8, 255}
	if hot && enabled {
		top, bot = color.RGBA{0xe6, 0xf8, 0xff, 255}, color.RGBA{0x58, 0xb8, 0xff, 255}
	}
	if !enabled {
		top, bot = color.RGBA{0xe4, 0xe8, 0xec, 255}, color.RGBA{0xa8, 0xb0, 0xb8, 255}
	}
	g := vgrad{r.Min.Y, r.Max.Y, top, bot}
	roundRect(dst, r, rad, func(y int) color.RGBA { return g.At(0, y).(color.RGBA) })
	// gloss on the upper half
	gl := image.Rect(r.Min.X+3, r.Min.Y+2, r.Max.X-3, r.Min.Y+r.Dy()/2)
	roundRect(dst, gl, rad-3, func(y int) color.RGBA { return color.RGBA{255, 255, 255, 90} })
	ring(dst, r, rad, 1.5, color.RGBA{0x1d, 0x4f, 0x8f, 255})
	ring(dst, r.Inset(2), rad-2, 1, color.RGBA{255, 255, 255, 160})
	f := face(size, true)
	tx := r.Min.X + (r.Dx()-textWidth(f, label))/2
	ty := r.Min.Y + (r.Dy()+ascent(f)-descent(f))/2
	if enabled {
		drawFancy(dst, f, tx, ty, label, color.RGBA{0xff, 0xf0, 0x80, 255}, color.RGBA{0xff, 0x80, 0x10, 255}, color.RGBA{0x7a, 0x2a, 0x00, 255}, 1)
	} else {
		drawText(dst, f, tx, ty, label, solid(color.RGBA{0x70, 0x78, 0x80, 255}))
	}
}

// blit draws src (1x art) at p (window points, alpha over); at 2x the art is scaled up once and cached.
func blit(dst *image.RGBA, src *image.RGBA, p image.Point) {
	if src == nil {
		return
	}
	src = scaledArt(src)
	draw.Draw(dst, src.Bounds().Sub(src.Bounds().Min).Add(p.Mul(uiScale)), src, src.Bounds().Min, draw.Over)
}

var (
	scaledMu sync.Mutex
	scaled   = map[[2]uintptr]*image.RGBA{}
)

func scaledArt(src *image.RGBA) *image.RGBA {
	if uiScale == 1 {
		return src
	}
	k := [2]uintptr{uintptr(unsafe.Pointer(src)), uintptr(uiScale)}
	scaledMu.Lock()
	defer scaledMu.Unlock()
	if s, ok := scaled[k]; ok {
		return s
	}
	b := src.Bounds()
	s := image.NewRGBA(image.Rect(0, 0, b.Dx()*uiScale, b.Dy()*uiScale))
	xdraw.CatmullRom.Scale(s, s.Bounds(), src, b, xdraw.Src, nil)
	scaled[k] = s
	return s
}

// dim darkens every visible pixel of dst (the settings panel's backdrop).
func dim(dst *image.RGBA, k float64) {
	for i := 0; i < len(dst.Pix); i += 4 {
		for j := 0; j < 3; j++ {
			dst.Pix[i+j] = uint8(float64(dst.Pix[i+j]) * k)
		}
	}
}

// plainBackground stands in for the IGG art when it is not built in: panels in the same places, a GodsWar title.
func plainBackground(img *image.RGBA) {
	outer := image.Rect(36, 86, 770, 540)
	g := vgrad{outer.Min.Y, outer.Max.Y, color.RGBA{0xd8, 0xf0, 0xff, 255}, color.RGBA{0xf8, 0xfc, 0xff, 255}}
	roundRect(img, outer, 18, func(y int) color.RGBA { return g.At(0, y).(color.RGBA) })
	ring(img, outer, 18, 3, color.RGBA{0x3c, 0x8c, 0x3c, 255})
	drawFancy(img, face(56, true), 250, 72, "GodsWar", color.RGBA{0xff, 0xf0, 0x60, 255}, color.RGBA{0x40, 0xb0, 0x20, 255}, color.RGBA{0x10, 0x30, 0x70, 255}, 3)
	for _, p := range []struct {
		r     image.Rectangle
		title string
	}{{image.Rect(80, 152, 354, 266), "Event Summary"}, {image.Rect(78, 280, 352, 438), "Servers"}} {
		roundRect(img, p.r, 10, func(int) color.RGBA { return color.RGBA{0xd2, 0xf0, 0xf8, 255} })
		ring(img, p.r, 10, 1.5, color.RGBA{0x58, 0xa8, 0xd8, 255})
		drawFancy(img, face(18, true), p.r.Min.X+20, p.r.Min.Y+24, p.title, color.RGBA{0xff, 0xe0, 0x60, 255}, color.RGBA{0xf0, 0x80, 0x10, 255}, color.RGBA{0x6a, 0x22, 0x00, 255}, 1)
	}
	box := image.Rect(380, 172, 734, 422)
	roundRect(img, box, 12, func(int) color.RGBA { return color.RGBA{0x58, 0xa4, 0xc8, 255} })
	roundRect(img, box.Inset(8), 4, func(int) color.RGBA { return color.RGBA{255, 255, 255, 255} })
}

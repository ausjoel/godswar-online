//go:build windows

package main

// The Windows launcher window: a layered pop-up (per-pixel alpha, so the vine border is shaped like the original
// launcher), plain Win32 through syscall, no cgo.

import (
	"errors"
	"image"
	"runtime"
	"sync"
	"syscall"
	"unsafe"
)

var (
	user32               = syscall.NewLazyDLL("user32.dll")
	gdi32                = syscall.NewLazyDLL("gdi32.dll")
	kernel32             = syscall.NewLazyDLL("kernel32.dll")
	pRegisterClassExW    = user32.NewProc("RegisterClassExW")
	pCreateWindowExW     = user32.NewProc("CreateWindowExW")
	pDefWindowProcW      = user32.NewProc("DefWindowProcW")
	pGetMessageW         = user32.NewProc("GetMessageW")
	pTranslateMessage    = user32.NewProc("TranslateMessage")
	pDispatchMessageW    = user32.NewProc("DispatchMessageW")
	pPostMessageW        = user32.NewProc("PostMessageW")
	pSendMessageW        = user32.NewProc("SendMessageW")
	pPostQuitMessage     = user32.NewProc("PostQuitMessage")
	pDestroyWindow       = user32.NewProc("DestroyWindow")
	pShowWindow          = user32.NewProc("ShowWindow")
	pSetForegroundWindow = user32.NewProc("SetForegroundWindow")
	pUpdateLayeredWindow = user32.NewProc("UpdateLayeredWindow")
	pGetDC               = user32.NewProc("GetDC")
	pReleaseDC           = user32.NewProc("ReleaseDC")
	pLoadCursorW         = user32.NewProc("LoadCursorW")
	pLoadIconW           = user32.NewProc("LoadIconW")
	pSetCapture          = user32.NewProc("SetCapture")
	pReleaseCapture      = user32.NewProc("ReleaseCapture")
	pTrackMouseEvent     = user32.NewProc("TrackMouseEvent")
	pGetSystemMetrics    = user32.NewProc("GetSystemMetrics")
	pGetWindowRect       = user32.NewProc("GetWindowRect")
	pCreateCompatibleDC  = gdi32.NewProc("CreateCompatibleDC")
	pCreateDIBSection    = gdi32.NewProc("CreateDIBSection")
	pSelectObject        = gdi32.NewProc("SelectObject")
	pDeleteObject        = gdi32.NewProc("DeleteObject")
	pDeleteDC            = gdi32.NewProc("DeleteDC")
	pGetModuleHandleW    = kernel32.NewProc("GetModuleHandleW")
)

const (
	wmDestroy       = 0x0002
	wmClose         = 0x0010
	wmMouseMove     = 0x0200
	wmLButtonDown   = 0x0201
	wmLButtonUp     = 0x0202
	wmMouseLeave    = 0x02A3
	wmNCLButtonDown = 0x00A1
	wmApp           = 0x8000
	htCaption       = 2
)

type wndClassEx struct {
	size, style                uint32
	wndProc                    uintptr
	clsExtra, wndExtra         int32
	instance, icon, cursor, bg uintptr
	menuName, className        *uint16
	iconSm                     uintptr
}

type winMsg struct {
	hwnd    uintptr
	message uint32
	wParam  uintptr
	lParam  uintptr
	time    uint32
	x, y    int32
	private uint32
}

type bitmapInfo struct {
	size                   uint32
	width, height          int32
	planes, bitCount       uint16
	compression, sizeImage uint32
	xPels, yPels           int32
	clrUsed, clrImportant  uint32
}

type trackMouse struct {
	size, flags uint32
	hwnd        uintptr
	hover       uint32
}

type winWindow struct {
	hwnd     uintptr
	mu       sync.Mutex
	frame    []byte
	w, h     int
	onEvent  func(kind, x, y int) bool
	tracking bool
}

var theWin *winWindow

func (w *winWindow) Present(img *image.RGBA) {
	b := make([]byte, len(img.Pix))
	for i := 0; i < len(b); i += 4 { // RGBA -> BGRA, already premultiplied
		b[i], b[i+1], b[i+2], b[i+3] = img.Pix[i+2], img.Pix[i+1], img.Pix[i], img.Pix[i+3]
	}
	w.mu.Lock()
	w.frame = b
	w.mu.Unlock()
	pPostMessageW.Call(w.hwnd, wmApp+1, 0, 0)
}

func (w *winWindow) Quit() { pPostMessageW.Call(w.hwnd, wmApp+2, 0, 0) }

// show puts the latest frame on screen with UpdateLayeredWindow.
func (w *winWindow) show() {
	w.mu.Lock()
	frame := w.frame
	w.mu.Unlock()
	if frame == nil {
		return
	}
	screen, _, _ := pGetDC.Call(0)
	defer pReleaseDC.Call(0, screen)
	mem, _, _ := pCreateCompatibleDC.Call(screen)
	defer pDeleteDC.Call(mem)
	bi := bitmapInfo{size: 40, width: int32(w.w), height: -int32(w.h), planes: 1, bitCount: 32}
	var bits uintptr
	bmp, _, _ := pCreateDIBSection.Call(mem, uintptr(unsafe.Pointer(&bi)), 0, uintptr(unsafe.Pointer(&bits)), 0, 0)
	if bmp == 0 || bits == 0 {
		return
	}
	defer pDeleteObject.Call(bmp)
	copy(unsafe.Slice((*byte)(unsafe.Pointer(bits)), len(frame)), frame)
	old, _, _ := pSelectObject.Call(mem, bmp)
	defer pSelectObject.Call(mem, old)
	var r [4]int32
	pGetWindowRect.Call(w.hwnd, uintptr(unsafe.Pointer(&r)))
	pos := [2]int32{r[0], r[1]}
	size := [2]int32{int32(w.w), int32(w.h)}
	src := [2]int32{0, 0}
	blend := [4]byte{0, 0, 255, 1} // AC_SRC_OVER, 0, alpha 255, AC_SRC_ALPHA
	pUpdateLayeredWindow.Call(w.hwnd, screen, uintptr(unsafe.Pointer(&pos)), uintptr(unsafe.Pointer(&size)), mem,
		uintptr(unsafe.Pointer(&src)), 0, uintptr(unsafe.Pointer(&blend)), 2 /* ULW_ALPHA */)
}

func signed16(v uintptr) int { return int(int16(uint16(v))) }

func wndProc(hwnd uintptr, msg uint32, wp, lp uintptr) uintptr {
	w := theWin
	switch msg {
	case wmApp + 1:
		w.show()
		return 0
	case wmApp + 2:
		pDestroyWindow.Call(hwnd)
		return 0
	case wmMouseMove:
		if !w.tracking {
			tm := trackMouse{size: uint32(unsafe.Sizeof(trackMouse{})), flags: 2 /* TME_LEAVE */, hwnd: hwnd}
			pTrackMouseEvent.Call(uintptr(unsafe.Pointer(&tm)))
			w.tracking = true
		}
		w.onEvent(evMove, signed16(lp), signed16(lp>>16))
		return 0
	case wmMouseLeave:
		w.tracking = false
		w.onEvent(evLeave, 0, 0)
		return 0
	case wmLButtonDown:
		if w.onEvent(evDown, signed16(lp), signed16(lp>>16)) {
			pSetCapture.Call(hwnd)
		} else {
			pReleaseCapture.Call()
			pSendMessageW.Call(hwnd, wmNCLButtonDown, htCaption, 0)
		}
		return 0
	case wmLButtonUp:
		pReleaseCapture.Call()
		w.onEvent(evUp, signed16(lp), signed16(lp>>16))
		return 0
	case wmClose:
		w.onEvent(evClose, 0, 0)
		return 0
	case wmDestroy:
		pPostQuitMessage.Call(0)
		return 0
	}
	r, _, _ := pDefWindowProcW.Call(hwnd, uintptr(msg), wp, lp)
	return r
}

// runWindow creates the window and runs its message loop on this thread until Quit.
func runWindow(w, h int, title string, onEvent func(kind, x, y int) bool, ready func(window)) error {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	if err := user32.Load(); err != nil {
		return errNoWindow
	}
	inst, _, _ := pGetModuleHandleW.Call(0)
	cls, _ := syscall.UTF16PtrFromString("GodsWarLauncher")
	ttl, _ := syscall.UTF16PtrFromString(title)
	cur, _, _ := pLoadCursorW.Call(0, 32512)
	icon, _, _ := pLoadIconW.Call(inst, 1) // the exe's icon resource, when it has one
	wc := wndClassEx{wndProc: syscall.NewCallback(wndProc), instance: inst, cursor: cur, icon: icon, iconSm: icon, className: cls}
	wc.size = uint32(unsafe.Sizeof(wc))
	if r, _, _ := pRegisterClassExW.Call(uintptr(unsafe.Pointer(&wc))); r == 0 {
		return errors.New("RegisterClassEx failed")
	}
	sw, _, _ := pGetSystemMetrics.Call(0)
	sh, _, _ := pGetSystemMetrics.Call(1)
	x, y := (int(sw)-w)/2, (int(sh)-h)/2
	theWin = &winWindow{w: w, h: h, onEvent: onEvent}
	const exStyle = 0x00080000 | 0x00040000            // WS_EX_LAYERED | WS_EX_APPWINDOW
	const style = 0x80000000 | 0x00080000 | 0x00020000 // WS_POPUP | WS_SYSMENU | WS_MINIMIZEBOX
	hwnd, _, _ := pCreateWindowExW.Call(exStyle, uintptr(unsafe.Pointer(cls)), uintptr(unsafe.Pointer(ttl)), style,
		uintptr(x), uintptr(y), uintptr(w), uintptr(h), 0, 0, inst, 0)
	if hwnd == 0 {
		return errors.New("CreateWindowEx failed")
	}
	theWin.hwnd = hwnd
	go ready(theWin)
	pShowWindow.Call(hwnd, 5)
	pSetForegroundWindow.Call(hwnd)
	var m winMsg
	for {
		r, _, _ := pGetMessageW.Call(uintptr(unsafe.Pointer(&m)), 0, 0, 0)
		if int32(r) <= 0 {
			break
		}
		pTranslateMessage.Call(uintptr(unsafe.Pointer(&m)))
		pDispatchMessageW.Call(uintptr(unsafe.Pointer(&m)))
	}
	return nil
}

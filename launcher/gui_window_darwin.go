//go:build darwin && cgo

package main

/*
#cgo CFLAGS: -x objective-c -fobjc-arc -mmacosx-version-min=11.0
#cgo LDFLAGS: -framework Cocoa
#include <stdlib.h>
void gwuiRun(int w, int h, const char *title);
void gwuiPresent(const void *rgba, int w, int h);
void gwuiQuit(void);
int gwuiScale(void);
*/
import "C"

import (
	"image"
	"runtime"
	"unsafe"
)

func init() { runtime.LockOSThread() } // AppKit must run on the main thread

var macEvent func(kind, x, y int) bool

//export gwuiEvent
func gwuiEvent(kind, x, y C.int) C.int {
	if macEvent != nil && macEvent(int(kind), int(x), int(y)) {
		return 1
	}
	return 0
}

type macWindow struct{}

func (macWindow) Present(img *image.RGBA) {
	C.gwuiPresent(unsafe.Pointer(&img.Pix[0]), C.int(img.Rect.Dx()), C.int(img.Rect.Dy()))
}
func (macWindow) Quit() { C.gwuiQuit() }

// runWindow shows the window and runs the AppKit loop on the main thread until Quit.
func runWindow(w, h int, title string, onEvent func(kind, x, y int) bool, ready func(window)) error {
	macEvent = onEvent
	// Retina: draw at the screens' backing scale (2) so text and edges are sharp (review d147).
	if s := int(C.gwuiScale()); s >= 2 {
		uiScale = scaleFromEnv(2)
	} else {
		uiScale = scaleFromEnv(1)
	}
	t := C.CString(title)
	defer C.free(unsafe.Pointer(t))
	go ready(macWindow{})
	C.gwuiRun(C.int(w), C.int(h), t)
	return nil
}
